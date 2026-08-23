# backend

One .NET process that is three things at once: a forward proxy that intercepts
TLS and rewrites bodies, a WebAPI, and a live telemetry stream for the
dashboard. Solution `Backend.slnx`, one product project
(`src/SeniorsInTheMiddle.Proxy`) and one test project (`test/Backend.Tests`).

The personal-data detection itself is not here. It runs in the Python services
under `/services`, reached over unix sockets; see
[services/README.md](../services/README.md). Without them the proxy still
forwards and traces, it just finds nothing to replace — which is the normal
state on a Windows dev box.

## The one thing to know first

A body cannot be rewritten in one direction. Replacing a name on the way out
only helps if the same name comes back on the way in, otherwise the user reads
someone else's name on their own screen. So the proxy keeps a map from stand-in
to real value, and everything else in here follows from where that map lives and
how long.

The map is keyed by **client and destination host**, and outlives the exchange
that filled it (`Proxy:AnonymizerTtlHours`, 48 by default). It has to: a chat
client posts a message in one request and draws that message back in a
different one. It is also the most dangerous structure in the process — applied
to a body it was not built for, it writes a real name into an unrelated
response. The key is what contains that, not the lifetime.

## Ports

Three listeners, one job each (`Proxy:HttpPort`, `Proxy:HttpsPort`,
`Proxy:ApiPort` — see `Forwarding/ProxyPorts.cs`). Which listener a request
arrived on decides how it is treated.

| port   | default | what answers there                                              |
| ------ | ------- | --------------------------------------------------------------- |
| proxy  | 3128    | CONNECT tunnels and absolute-form HTTP, in the clear             |
| proxy  | 3127    | the same, inside TLS — for clients configured with an `HTTPS` proxy. `0` turns it off |
| api    | 8080    | WebAPI, Swagger, telemetry hub. Never proxies                    |

Both proxy ports run the same connection middleware; the only difference is
that TLS is terminated first on 3127, so the sniffing sees the same plaintext
request line either way. Its certificate is signed by our own CA, so a device
that already trusts `/ca.crt` reaches it without a second warning.

`ProxyPortGuard` keeps the API off the proxy ports. Only `/ca.crt` and
`/proxy.pac` answer there — what a device needs to be set up, and nothing else.

## How a request gets through

```
 client
   │
   ├─ CONNECT example.com:443 ──▶ ConnectProxyMiddleware
   │                                │ MITM handshake with a certificate minted for the host
   │                                │
   │                                ├─ decrypted bytes look like HTTP/1.x
   │                                │    → swap the connection transport for the decrypted
   │                                │      stream, hand it back to Kestrel, and the requests
   │                                │      inside the tunnel take the plaintext path below
   │                                │
   │                                └─ anything else (mail, database, …)
   │                                     → StreamProxy: byte-for-byte copy, never decoded
   │
   └─ GET http://example.com/ ───▶ ForwardProxyMiddleware  (absolute form only)
                                     │
                                     ▼
                                  ForwardProxy
                                     │ Detours?  → 302, never forwarded
                                     ▼
                     YARP IHttpForwarder + ForwardProxyTransformer
                                     │
        request  ─▶ decompress ─▶ detect (pii service) ─▶ replace ─▶ upstream
        response ◀─ restore    ◀─ decompress          ◀────────────────┘
                                     │
                                     ▼
                              ExchangeTrace ─▶ TelemetryPump ─▶ SignalR hub ─▶ dashboard
```

A CONNECT is not a promise that HTTP follows, which is why the opaque branch
exists: an HTTP parser would answer 400 to a mail session that used to work.

### What is *not* inspected, and why

Three separate escape hatches, easy to confuse:

| mechanism            | config              | decrypted? | traced? | forwarded? |
| -------------------- | ------------------- | ---------- | ------- | ---------- |
| `InterceptionBypass` | `Proxy:BypassHosts` | no         | barely  | yes, as a raw tunnel |
| `InspectionScope`    | `Proxy:InspectOnly` | yes        | yes     | yes, body untouched  |
| `Detours`            | `Proxy:Detours`     | n/a        | yes     | **no**, answered with a 302 |

`BypassHosts` exists for bot management, not privacy. A managed challenge scores
the TLS handshake itself against the User-Agent the request claims; interception
replaces the browser's handshake with this process's, the two stop agreeing, and
the challenge can never be passed. Nothing in the body pipeline can fix that,
because the verdict is reached before a single HTTP byte is sent. The cost is
total: a bypassed host is invisible to the proxy.

`InspectOnly` inverts the default for one host: nothing is inspected except the
paths named. A site is not one thing — on `chatgpt.com` the prompt goes to a
handful of endpoints under `/backend-api/`, and the rest of the origin is
scripts, feature flags and session polling. None of that carries anything a
person typed, and every body scanned is a body that can come back subtly wrong.

`Detours` answers locally instead of forwarding, for traffic nobody at the
device asked for (a compromised box farming YouTube views). Nothing matched is
inspected, so keep the paths narrow.

### Where the rewrite happens, and why it looks odd

`ForwardProxyTransformer` rewrites the **request** body on
`HttpContext.Request`, not on the outgoing `HttpRequestMessage`. That is not a
style choice: YARP assigns its own streaming content before the transformer
runs and refuses any replacement, reporting the refusal as a failed request
creation answered with 502 — so it reads like an unreachable destination rather
than a bug.

The response is the mirror image and easier: `HttpResponseMessage.Content` has
no such guard, so the body is replaced directly and the framing headers
(`Content-Length`, `Content-Encoding`, digests) are corrected afterwards.
Digests are **dropped, not recomputed** — recomputing would re-assert an
integrity claim on the proxy's behalf that neither end ever made. Bodies behind
a signature header are left alone entirely.

A body must be buffered whole before it can be rewritten, because the
replacement's length is not known until it exists. `Proxy:MaxMutableBodyBytes`
(1 MiB) bounds that, measured **after** decompression — a few kilobytes of gzip
can expand to gigabytes. Above the limit the bytes stream through untouched and
the skip is logged. Server-sent events are the exception: `RestoringStream`
applies the rewrite chunk by chunk so a live stream stays live.

## Layout

```
src/SeniorsInTheMiddle.Proxy/
  Program.cs                     composition root, in reading order
  InfrastructureRegistrations.cs OpenAPI, JWT bearer, CORS, middleware order
  Forwarding/                    the proxy
    Registrar.cs                 DI + the three Kestrel listeners
    ConnectProxyMiddleware.cs    CONNECT → intercepted TLS, or a byte tunnel
    ForwardProxyMiddleware.cs    absolute form → forward, origin form → pipeline
    ForwardProxy.cs              per-request YARP call + telemetry
    ForwardProxyTransformer.cs   both halves of one exchange
    RestoringStream.cs           streaming rewrite, for SSE
    MitmCertificateProvider.cs   the CA and the per-host certificates
    InterceptionBypass.cs        ┐
    InspectionScope.cs           ├ what is left alone (table above)
    Detours.cs                   ┘
    Tokenizer/                   the rewrite itself
      ReplacerService.cs         IBodyMutationFactory: one Exchange per pair
      TokenDetectionService.cs   asks the pii service, folds the findings
      TokenAnonymizerService.cs  the stand-in map, per client and host
      AnonymizerVault.cs         keeps those maps alive across exchanges
  Telemetry/                     the dashboard stream
    TelemetryEvent.cs            the wire contract (mirrors the frontend schemas)
    TelemetryPump.cs             bounded queue, single reader
    TelemetryHub.cs              SignalR, one-way
  Services/                      unix-socket clients for the python services
  Auth/                          JWT issuing, in-memory users, demo seeder
test/Backend.Tests/
  Unit/                          one class under test each
  Integration/                   WebApplicationFactory, real sockets, real TLS
```

## Telemetry

Everything the dashboard shows is one `TelemetryEvent` subtype, serialized with
a `type` discriminator. `TelemetryEvent.cs` mirrors the valibot schemas in
`frontend/src/protocol/types.ts` field for field — a frame that does not match
is dropped by the browser rather than shown, so the two files change together.
`TelemetryJson.ProtocolVersion` is announced in the hello frame.

`ITelemetrySink` is called on request and tunnel threads, so it never blocks,
never throws and never waits for a dashboard. `TelemetryPump` is a bounded
queue with a single reader: a slow dashboard stalls the queue, not the proxy,
and a full queue drops the **newest** event — dropping the oldest would leave
the dashboard with completions for rows it never received.

`PrivacyAssessor` is off the request path entirely. The privacy-check service
samples an MCMC chain and takes seconds, which no response can wait for, so the
answer is scheduled and arrives as its own event later. One check at a time;
one that cannot start is skipped and says so.

Two guards on the hub, because CORS does not apply to a WebSocket handshake:
`TelemetryOriginGuard` checks `Origin` before authentication (a foreign origin
is refused whether or not it carries a valid token), and the hub separately
requires a signed-in user. A request with no `Origin` is not a browser and
passes — that is curl, and the .NET client the tests use.

## Auth

Deny by default: the fallback authorization policy requires an authenticated
user, so a new endpoint is private unless someone deliberately publishes it.
The handful that opt out with `AllowAnonymous` each carry a reason at the call
site — `/health`, `/healthz`, the OpenAPI document, and the two bootstrap
endpoints a device needs before anyone has an account at all.

| route                        | anonymous | what it is                            |
| ---------------------------- | --------- | ------------------------------------- |
| `POST /api/v1/auth/login`    | yes       | username + password → JWT             |
| `POST /api/v1/auth/register` | yes       | self-registration                     |
| `GET /api/v1/auth/demo-account` | yes    | seeded credentials, if `Advertise`    |
| `GET /api/v1/auth/me`        | no        | current user                          |
| `GET /ca.crt`                | yes       | the CA a device must trust            |
| `GET /proxy.pac`             | yes       | auto-config, built from the request host |
| `GET /health`                | yes       | liveness, no dependencies             |
| `GET /healthz`               | yes       | pings every configured python service |
| `/hub/telemetry`             | no        | SignalR, token in the query string    |

Accounts live in memory and are gone on every restart, which is why
`UserSeeder` recreates the demo account at startup. Passwords are PBKDF2 hashes
even so — in-memory still reaches a crash dump.

The hub takes its token from `?access_token=` because a browser cannot put an
`Authorization` header on a WebSocket handshake. That is scoped to the hub path
on purpose: query strings end up in access logs.

## Python services

`Services/` holds one `ServiceConnection` per python service, each on its own
unix socket, opened on first use and shared by every caller (the protocol
multiplexes by id). A broken socket — supervisord restarted the daemon — is
dropped and the next call reconnects, so a bounced service never needs the
proxy restarted.

A service with an empty `SocketPath` is **disabled**, not broken: `IsEnabled`
is false, calls throw `ServiceUnavailableException`, and `/healthz` reports it
as disabled without going Unhealthy. That is the normal Windows dev state.

## Configuration

Environment variables use `__` for `:` (`Proxy__HttpPort`,
`Services__Pii__SocketPath`).

| key                            | default   | what it decides                                    |
| ------------------------------ | --------- | -------------------------------------------------- |
| `Proxy:HttpPort`               | 3128      | plain proxy listener                                |
| `Proxy:HttpsPort`              | 3127      | TLS proxy listener; `0` disables                    |
| `Proxy:ApiPort`                | 8080      | WebAPI, Swagger, hub                                |
| `Proxy:HostNames`              | `[]`      | extra names, added to the certificate               |
| `Proxy:BypassHosts`            | Turnstile | not decrypted at all                                |
| `Proxy:InspectOnly`            | chatgpt.com | host → the only paths inspected there             |
| `Proxy:Detours`                | youtube.com | host → paths answered with a 302                  |
| `Proxy:MaxMutableBodyBytes`    | 1048576   | largest body buffered for rewrite, after decompression |
| `Proxy:AnonymizerTtlHours`     | 48        | how long a client's stand-in map lives; `0` pins it to one exchange |
| `Proxy:MaxAnonymizerClients`   | 512       | how many such maps at once, LRU past that           |
| `Mitm:CertificatePath`         | `mitm-ca.pfx` | where the CA is kept; generated if absent       |
| `Mitm:CertificatePassword`     | empty     | for the pfx                                         |
| `Cors:AllowedOrigins`          | —         | the dashboard's origins, no trailing slash          |
| `Jwt:Key`                      | empty     | HMAC signing key; **required**, no fallback         |
| `Jwt:Issuer` / `Jwt:Audience`  | Backend / BackendApp | validated on every token                |
| `Services:Pii:SocketPath`      | empty     | empty disables PII detection                        |
| `Services:PrivacyCheck:SocketPath` | empty | empty disables the risk gauge                       |
| `Auth:SeedUser:*`              | demo/demo | the account recreated at startup                    |

`Auth:SeedUser:Advertise` serves those credentials at
`/api/v1/auth/demo-account` so the login screen can prefill them. It is on for
the public demo, where the password is in this repository anyway and the
traffic behind it is synthetic. Set `Auth__SeedUser__Advertise=false` to take
the endpoint dark without a redeploy, and replace the password before this ever
sees a real household.

## Running it

```bash
dotnet run --project backend/src/SeniorsInTheMiddle.Proxy
```

API on 5284 in Development (8080 is taken by `npm run preview`), proxy on 3128
and 3127. There are no unix sockets on Windows, so both service socket paths
stay empty and the proxy runs without detection: traffic is still intercepted,
traced and shown in the dashboard, but nothing is replaced.

For the full path, build the image — build context is the **repository root**,
because the python services and the uv lock come from there:

```bash
docker build -f backend/Dockerfile -t sitm-proxy .
```

```bash
docker run --rm -v sitm-ca:/app/certs -p 3128:3128 -p 8080:8080 -e Jwt__Key=$(openssl rand -base64 32) sitm-proxy
```

Mount that volume. The MITM CA is generated on first start into `/app/certs`,
and without a volume it lives in the container's writable layer: a redeploy
mints a new CA, and every device then has to trust it again — an OS-level step
on each one, not a browser click. An existing CA can be supplied instead with
`Mitm__CertificatePath` and `Mitm__CertificatePassword`.

supervisord runs the proxy and both python daemons in that container
(`backend/supervisord.conf`); `docker exec <id> supervisorctl status` shows
which of them is up. `curl localhost:8080/healthz` pings every configured
service.

Everything together, the way it deploys, is `integration/docker-compose.yml`.

## Tests

```bash
dotnet test backend/Backend.slnx
```

MSTest on Microsoft.Testing.Platform (`global.json` pins the runner).
`Unit/` covers one class each; `Integration/` runs the real host through
`WebApplicationFactory`, including real TLS interception and real sockets
(`ForwardingHarness`, `TunnelHarness`). The product project makes its internals
visible to the test assembly, so a `sealed class` with no public surface is
still testable directly.

The build has no warnings. Keep it that way.

## Extending it

**A new python service** — the full checklist is in
[services/README.md](../services/README.md); on this side it is a name in
`ServiceConnections.KnownServices` and a typed client next to `Services/Pii/`.

**A different rewrite** — implement `IBodyMutationFactory` and
`IExchangeBodyMutation` next to `PassthroughMutationFactory`, then swap the
registration in `Forwarding/Registrar.cs`. Decide explicitly what happens to a
body the mutation cannot parse: returning null forwards it unchanged, throwing
fails the exchange. Both are legitimate; neither may be left to an exception
nobody meant to throw.

**A new telemetry event** — add the record and its `[JsonDerivedType]` in
`TelemetryEvent.cs`, add the matching valibot schema in
`frontend/src/protocol/types.ts`, and bump `TelemetryJson.ProtocolVersion`.
Serialize through `TelemetryJson`, never by runtime type, or the discriminator
is silently left out.

**A new endpoint** — it is authenticated unless you say otherwise. If it has to
be anonymous, write down at the call site why, and check whether it should also
be reachable on the proxy ports (`ProxyPortGuard`) — almost nothing should.
