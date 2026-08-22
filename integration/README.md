# Integration harness

A testing harness for `backend/Dockerfile`. It runs the proxy image exactly as it is
built and keeps traffic flowing through it: one app that sends, one app that receives,
and a small UI to watch and steer the flow.

```bash
cd integration
cp .env.example .env
docker compose up --build
```

Then open **http://localhost:3100**.

## What runs

| Service | What it is |
| --- | --- |
| `certgen` | One-shot. Issues the harness CA and the receiver's HTTPS certificate into the `pki` volume. |
| `proxy` | **The thing under test.** Built from `../backend/Dockerfile`, unmodified. Proxy traffic on 3128, the same over TLS on 3127, the API and telemetry stream on 8080. |
| `receiver` | An ordinary destination host on 3000 (HTTP) and 3443 (HTTPS). Knows nothing about the proxy. |
| `sender` | A device configured to use the proxy. Generates traffic and serves the testing UI on 3100. |
| `dashboard` | Optional (`--profile dashboard`). The SPA from `../frontend`, on 8081. |

Nothing outside `integration/` is touched, and nothing is injected into the proxy image:
everything it needs arrives as environment variables and mounted volumes. That is
deliberate — a harness that patches the image cannot tell you the image is broken.

## What the traffic looks like

The sender picks from a weighted table, seeded so a run can be repeated (`SEED`):

- **passthrough** — `/assets/app.css`, `app.js`, `logo.svg`, `photo.jpg`. Content types
  whose bodies a proxy should never read. The JPEG is deliberately not valid UTF-8, which
  exercises the Base64 branch in `StreamProxy`.
- **clean JSON** — a body worth reading with nothing personal in it.
- **personal data** — Swiss-shaped fixtures matching the `EntityKind` union in
  `frontend/src/protocol/types.ts`: names, AHV numbers, IBANs, addresses, `+41` phone
  numbers, emails, birthdates, conditions, insurers. AHV numbers and IBANs carry correct
  check digits, so a detector that verifies them still finds them.
- **awkward cases** — chunked responses, a slow upstream, 4xx/5xx statuses, and a body
  larger than one 8 KiB proxy chunk.

Each request goes out one of two ways, in the `HTTPS_RATIO` mix:

```
http     GET http://receiver.sitm.local:3000/api/v1/status HTTP/1.1
         absolute form -> ForwardProxy forwards it

https    CONNECT receiver.sitm.local:3443 HTTP/1.1
         -> ConnectProxyMiddleware intercepts, mints a certificate, tunnels
```

And each of those reaches the proxy one of two ways, in the `PROXY_TLS_RATIO` mix:

```
plain    TCP to proxy:3128, the request line in the clear

tls      TLS to proxy:3127 first -- the "HTTPS proxy" a browser can be configured
         with -- then the same request line inside it. Verified against the proxy's
         own CA, with `proxy` as SNI, the way a device would.
```

The two draws are independent, so all four combinations occur, including CONNECT inside
a TLS connection: TLS in TLS. The table shows the transport in the `via` column and the
counters split it out, so a regression in the TLS listener — wrong names in its
certificate, HTTP/2 negotiated so the CONNECT sniffing never sees a request line, the
decrypted request not reaching the forwarder — shows up as failures under `tls` only,
while `plain` stays green.

There is no proxy library in the sender. Both request shapes and both transports are
written by hand with Node core modules, because those exact bytes are what the proxy
parses.

## The two numbers that matter

The testing UI shows both ends of every exchange:

- **Sender → round trip.** Every identifier the client sent should come back to the
  client unchanged. Redaction that loses data would show up here as a broken round trip.
- **Receiver → raw PII bodies.** The destination host reports how many bodies still
  carried real identifiers. Today that number equals the number of PII requests, because
  the proxy does not redact yet. **Driving it to zero while the round trip stays intact
  is the whole product.**

The receiver cannot recognise a person's name by shape, so the sender declares the names
it used in an `X-Harness-Names` header. Both ends are the harness; nothing there is a
trust boundary.

## The testing UI

`http://localhost:3100` — one static HTML file with inline script, no build step.

- Live table of the last 200 exchanges; click a row to see the request body that left the
  machine and the response body that came back.
- `MITM` flag on a row means the client was handed a certificate the proxy minted rather
  than the receiver's own — interception actually happened.
- Rate, workers, HTTPS share, TLS-to-proxy share and PII share are editable while it runs.
  Pause and resume.
- **Freeze table** stops the redraw so a row stays still long enough to click. Traffic and
  counters carry on underneath.
- **Fire** one request of any scenario over either scheme and either connection to the
  proxy, to poke the proxy by hand. The detail pane shows both certificates the client
  saw: the proxy's own on a TLS connection, and the minted one inside a tunnel.

Behind it: `GET /api/stats`, `GET /api/events?since=`, `GET|POST /api/config`,
`GET /api/scenarios`, `POST /api/fire`, `GET /api/ca`.

## Certificates

Two chains, neither of which needs a change to the proxy image.

**The harness CA → the receiver.** `ConnectProxyMiddleware` validates upstream
certificates normally, so the proxy has to trust whoever signed the receiver.
`pki/gen-certs.sh` writes `bundle.pem` — the image's own CA bundle plus the harness CA —
and the proxy is pointed at it with `SSL_CERT_FILE`. .NET on Linux resolves trust through
OpenSSL, which honours that variable.

**The proxy's MITM CA → intercepted hosts, and the proxy itself.** The proxy generates it
on first start into `/app/certs`, which the harness mounts as a named volume — the volume
`backend/Dockerfile` asks for. The sender fetches the public certificate from `/ca.crt`,
converts the DER bytes to PEM in-process, and trusts it for every TLS handshake, exactly
as a real device would. The same CA signs the certificate the TLS proxy listener on 3127
presents; its subject alternative names come from `Proxy:HostNames` plus `localhost` and
the container's own name, which is why the compose file sets `Proxy__HostNames__0: proxy`
— the sender verifies that certificate against the name it connects to.
Waiting for that endpoint to answer doubles as the sender's readiness check: neither
`aspnet:10.0` nor `node:22-alpine` ships curl or wget, so a healthcheck would have to be
written in Node anyway.

## Knobs

All in `.env` (see `.env.example`), and the traffic ones are live-editable in the UI.

| Variable | Default | Meaning |
| --- | --- | --- |
| `PROXY_IMAGE` | *(build locally)* | Run against a pre-built image instead, e.g. a `ghcr.io` tag. |
| `JWT_KEY` | a throwaway value | `appsettings.json` ships `Jwt:Key` empty, and the app will not start without one. |
| `PROXY_LOG_LEVEL` | `Information` | Already logs decrypted tunnel chunks. `Debug` adds forwarding detail. |
| `RATE_PER_MINUTE` | `120` | Requests per minute across all workers. |
| `CONCURRENCY` | `4` | Workers. |
| `HTTPS_RATIO` | `0.5` | Share of requests that go through `CONNECT`. |
| `PROXY_TLS_RATIO` | `0.5` | Share of requests whose connection to the proxy is TLS (port 3127). Independent of `HTTPS_RATIO`. |
| `PROXY_TLS_PORT` | `3127` | The TLS proxy listener. `0` if the image under test has none; everything then goes plain. |
| `PII_RATIO` | `0.35` | Share of requests carrying personal data. |
| `SEED` | `20260822` | Same seed, same sequence. |
| `BURST` | `0` | One-shot mode: send N requests, print a summary, exit. |
| `MIN_SUCCESS_RATE` | `0.95` | What a burst run must reach to exit 0. |
| `STARTUP_TIMEOUT_MS` | `120000` | How long to wait for the proxy before failing the run. |

## When it does not come up

**`all predefined address pools have been fully subnetted`** — Docker, not the harness.
Its default address pools are used up by networks from other projects. `docker network ls`
to see them, `docker network prune` to remove the unused ones (they are recreated the next
time those projects start). Alternatively pin a subnet under `networks.harness.ipam`.

**The sender keeps restarting** — it waits `STARTUP_TIMEOUT_MS` for the proxy to answer
`/ca.crt`, then exits so compose restarts it. `docker compose logs proxy` will say why the
proxy is not answering; an empty `Jwt:Key` and a port already in use are the usual two.

**Every HTTPS request fails to verify** — the proxy minted a new CA, which is what
`docker compose down -v` causes. The sender notices and re-fetches `/ca.crt` within about
30 seconds (`proxy-ca-changed` in its log). If the failures persist past that, the chain
is genuinely broken rather than stale.

**Only `via tls` rows fail, `plain` is fine** — the TLS proxy listener on 3127 is the
problem, not the forwarding. `tls to proxy: TLS not authorized: ...` means its certificate
does not name `proxy` (check `Proxy__HostNames__0`) or was not signed by the CA the
sender fetched. A timeout or an immediate close after the handshake usually means the
listener negotiated something other than HTTP/1.1, or the decrypted bytes never reached
the CONNECT sniffing. Set `PROXY_TLS_RATIO=1` to make the run consist of nothing else.

## Checking it works

```bash
# Both schemes flowing, sender and receiver agreeing
curl -s http://localhost:3100/api/stats

# The proxy's own view: decrypted tunnel chunks prove interception end to end
docker compose logs proxy | grep "Client -> remote" | head

# The same path by hand, from outside the compose network
curl -sI --proxy http://localhost:3128 http://receiver.sitm.local:3000/api/v1/status

# ... and through the TLS proxy listener. curl needs the proxy's CA for the proxy itself
# (--proxy-cacert) and for the intercepted host (--cacert).
curl -s http://localhost:3128/ca.crt | openssl x509 -inform DER -out proxy-ca.pem
curl -sI --proxy https://localhost:3127 --proxy-cacert proxy-ca.pem --cacert proxy-ca.pem \
  https://receiver.sitm.local:3443/api/v1/status

# One-shot, for CI: exits non-zero if the proxy regresses
docker compose run --rm -e BURST=200 sender

# The same, but every request through the TLS listener -- the run that catches a broken
# :3127 on its own
docker compose run --rm -e BURST=200 -e PROXY_TLS_RATIO=1 sender
```

The CA volume contract is worth checking once: note the fingerprint in
`curl -s http://localhost:3100/api/ca`, then `docker compose down && docker compose up -d`
and check it again. It should be the same one — that is what the volume at `/app/certs`
is for. `docker compose down -v` drops it and mints a new CA, which on a real deployment
means every device has to trust a new root.

## The dashboard

```bash
docker compose --profile dashboard up
```

`http://localhost:8081`. Press save without typing anything — the setup screen already
defaults to a proxy on this machine, which is exactly what the harness runs:

| Field | Value |
| --- | --- |
| feed | **live proxy** |
| telemetry hub URL | `http://localhost:8080/hub/telemetry` |
| proxy host | `localhost` |
| proxy port | `3128` |
| CA URL | derived: `http://localhost:3128/ca.crt` |
| PAC URL | derived: `http://localhost:3128/proxy.pac` |

The hub is on the proxy's API port and the traffic goes through its proxy port; they are
two listeners in the same container, which is why those numbers differ.

The header should settle on `live · http://localhost:8080/hub/telemetry`. Every request the
sender drives through the proxy shows up as a row — which is why the fixtures are Swiss and
the identifiers have valid check digits rather than being lorem ipsum.

Only the plain-HTTP path reports so far, and only that a request started and finished.
Everything the dashboard draws about bodies and identifiers waits on the proxy pipeline;
until then rows say `not inspected` and HTTPS tunnels are silent.

The dashboard's origin has to appear in the proxy's `Cors:AllowedOrigins`, and not only for
CORS: the hub checks the Origin of its WebSocket handshake against the same list, because a
browser applies neither CORS nor a preflight to that handshake. `docker-compose.yml` sets
both `http://localhost:8081` and `http://localhost:5173`, so `npm run dev` in `../frontend`
works against the harness too. An origin that is missing gets a 403 and the header stays on
`reattaching`.

## Scope

Synthetic data, a throwaway CA, an unauthenticated proxy, and both apps trusting each
other's headers. This runs on a developer machine against a proof-of-concept proxy.
Nothing here belongs on a network you do not own.
