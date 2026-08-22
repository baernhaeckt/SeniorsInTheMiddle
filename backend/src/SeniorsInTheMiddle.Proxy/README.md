# SeniorsInTheMiddle.Proxy

One process. It serves:

- the **forward proxy** — absolute-form HTTP requests and HTTPS interception through
  HTTP/1.1 `CONNECT`, for arbitrary destination hosts and ports;
- the **WebAPI** under `/api/v1`, plus `/health`, `/openapi/v1.json` and `/swagger`;
- the **telemetry stream** the dashboard connects to: a SignalR hub at `/hub/telemetry`.

The dashboard SPA is a **separate, lightweight image** — see `frontend/Dockerfile`. It is
deployed as its own Container App and talks to this one cross-origin, so the CORS section
below matters.

## Ports

Three listeners, one role each. None of them does two jobs.

| Port   | Configured by     | Serves                                                        |
|--------|-------------------|---------------------------------------------------------------|
| `3128` | `Proxy:HttpPort`  | Proxy traffic, plus `/ca.crt` and `/proxy.pac`. Nothing else. |
| `3127` | `Proxy:HttpsPort` | The same proxy inside TLS. Set to `0` to disable.             |
| `8080` | `Proxy:ApiPort`   | The WebAPI, Swagger, the telemetry stream and `/healthz`. Never proxies. |

The three must differ; the app refuses to start otherwise rather than failing later with
an opaque "address already in use".

Under the `Development` launch profile the API is on `5284`, because `npm run preview` in
`../frontend` binds `8080` on a developer machine. The two proxy ports are the same
everywhere.

**A device is pointed at `3128` (or `3127`), never at `8080`.** Proxy traffic arrives in
absolute form (`GET http://example.com/ HTTP/1.1`) or as a `CONNECT` tunnel. Origin-form
requests on a proxy port are answered only for the two paths a device needs before it can
trust us — the CA certificate and the PAC file — and everything else there gets a `404`.
An absolute-form request aimed at this app's own address is answered locally rather than
proxied, so the proxy never loops back into itself.

The API port forwards nothing at all. An absolute-form request that lands there is treated
as an ordinary request, which keeps the dashboard's port from acting as an open proxy.

`3127` carries the proxy protocol with TLS terminated first, for a client configured with
an **`HTTPS` proxy** — the scheme browsers accept in a PAC file for a proxy that is itself
reached over TLS. Both proxy listeners are pinned to HTTP/1.1, because `CONNECT` is read as
a plain-text request line and ALPN would otherwise be free to negotiate HTTP/2 on the TLS
port.

`/proxy.pac` advertises both, most capable first:

```js
return "HTTPS proxy.example:3127; PROXY proxy.example:3128";
```

## CORS

The SPA runs on its own origin, so every call it makes here is cross-origin. Origins are
listed explicitly in `Cors:AllowedOrigins`, **not** wildcarded, because the SignalR
JavaScript client sends its negotiate request with credentials and a browser refuses a
credentialed response whose `Access-Control-Allow-Origin` is `*`.

The production frontend URL is in `appsettings.json`; local dev origins are in
`appsettings.Development.json`. Add another with
`Cors__AllowedOrigins__1=https://<host>` — no trailing slash, and never an empty value,
which blanks the entry. The effective list is printed at startup:

```
CORS allows https://seniorsinthemiddle-frontend....azurecontainerapps.io.
```

If a call from the SPA fails with an opaque network error in the browser, check that line
first.

The same list gates the telemetry hub, which is not a CORS matter at all — see
[Who may attach](#who-may-attach). A dashboard whose origin is missing gets a `403` on the
handshake and sits on `reattaching`.

## Telemetry

A SignalR hub at `/hub/telemetry`, on the API port. It is one-way: no callable methods, and
every frame is pushed as a **JSON string** on `event`.

The string matters. `JsonHubProtocol` serializes an argument by its runtime type, so a
`TelemetryEvent` sent as an object would go out without the polymorphic `type`
discriminator and the dashboard would reject every frame. `TelemetryJson.Serialize` writes
it through the base type; do not bypass it.

`Telemetry/TelemetryEvent.cs` mirrors the valibot schemas in
`frontend/src/protocol/types.ts` field for field. A frame that does not match is dropped by
the browser and counted in the header's badge rather than raised as an error, so a shape
mistake looks like an empty dashboard. `Backend.Tests/Unit/TelemetrySerialization.cs` pins
the wire shape.

### Emitting

Inject `ITelemetrySink` and call `Publish`. It never blocks, never throws and never waits
for a dashboard:

```csharp
telemetry.Publish(new RequestObserved(...));
telemetry.Warn("example.com could not be reached.");
```

Behind it is a bounded queue drained by one background reader that awaits each send, so
frames arrive in the order they happened and a slow dashboard stalls the queue rather than
a request. When the queue is full new events are dropped, counted, and reported into the
ticker — the queue drops the *newest*, because the protocol promises a `request.observed`
is followed by a `request.completed` and dropping the oldest would leave completions for
rows that were never sent.

### What is emitted when

One `Forwarding/ExchangeTrace.cs` per request is the only thing in the forwarding path that
publishes request and exchange events. `ForwardProxy.HandleAsync` creates it, the
transformer reports the HTTP-side facts to it (body buffered, dispatched, responded), and
the body mutation reports what it found and what it restored through the narrower
`IExchangeObserver` half of it. Intercepted HTTPS goes the same way, since a tunnelled
request re-enters `ForwardProxy` like any other.

The one thing to know: `request.observed` carries the treatment, and the treatment is only
known once the body has been scanned, so the trace holds the announcement back until the
decision is made and releases it just ahead of the first exchange event. It still carries
the time the request was seen. Whatever path a request takes, `request.observed` goes out
exactly once and first, a treated exchange always reaches `exchange.delivered` (steps it
never got to are filled in empty, so a packet on the band never stalls), and
`request.completed` is last. `Backend.Tests/Unit/ExchangeTraceTests.cs` pins that.

Treatments and their reasons, as the traffic list shows them:

| Treatment     | Reason                                                                          |
| ------------- | ------------------------------------------------------------------------------- |
| `passthrough` | `no body`, `rewriting disabled`, `signed payload (<header>)`, `larger than <n> bytes`, `<media type> not inspected`, `not forwarded: rewrite failed` (plus a `block` log line), `not inspected` (the forwarder gave up before the body was looked at) |
| `clean`       | `nothing found in <size> of <media type>`                                        |
| `treated`     | `<n> identifiers`, and the exchange events follow                                |

Entity `kind` is the detector's own name for the category (`PERSON`, `EMAIL_ADDRESS`,
`IBAN_CODE`, …), sent verbatim; the dashboard accepts any name. Bodies in events are cut at
`ExchangeTrace.MaxBodyChars`.

### Who may attach

A browser applies neither CORS nor a preflight to a WebSocket handshake, and the dashboard
connects with negotiation skipped, so `UseCors` never sees it. `TelemetryOriginGuard`
therefore checks the handshake's `Origin` against `Cors:AllowedOrigins` itself and answers
`403` for anything else — without it, any page a viewer happens to visit could open the hub
and read decrypted traffic off it. A request with no `Origin` is not a browser and passes,
which is how the tests and `curl` reach it.

### Settings

| Key | Default | Meaning |
|-----|---------|---------|
| `Telemetry:QueueCapacity` | `2048` | Frames buffered before new ones are dropped. |
| `Proxy:Name` | `Seniors in the Middle` | Shown in the dashboard header. |
| `Proxy:Region` | machine name | Shown in the dashboard header. |
| `Proxy:Policy` | `observe-only` | Shown in the dashboard header. |

## Python services

The PII detection (`services/pii_service`) and the re-identification risk check
(`services/privacy_check_service`) live in python processes that run next to this one
in the container image, supervised by supervisord. Each python service owns one unix
socket and is configured by name:

| Key | Default | Meaning |
|-----|---------|---------|
| `Services:Pii:SocketPath` | empty | Unix socket of the PII service. Empty disables it, which is the normal state on a Windows dev box. The image sets `/run/services/pii-service.sock`. |
| `Services:Pii:ConnectTimeoutSeconds` | `30` | How long the first connect keeps retrying while the daemon loads its model. |
| `Services:Pii:MaxFrameBytes` | `8388608` | Largest reply accepted; matches `SERVICE_MAX_FRAME_BYTES` on the python side. |
| `Services:PrivacyCheck:SocketPath` | empty | Unix socket of the privacy check service. The image sets `/run/services/privacy-check-service.sock`. |
| `Services:PrivacyCheck:ConnectTimeoutSeconds` | `60` | As above; this daemon loads a sentence-transformers model. |

`IPiiServiceClient` (`Services/Pii`) and `IPrivacyCheckServiceClient` (`Services/PrivacyCheck`)
are the typed clients; `ServiceConnection` behind them reconnects when supervisord restarts
the daemon. A `RiskCheckAsync` call runs an MCMC sampler on the python side and takes
tens of seconds; call it off the request path. `GET /healthz` on the API port pings
every configured service and answers 503 with one line per service when one is down.
A startup probe logs each service's `$info` once, so a wrong path shows up in the
container log immediately. The wire format is described in `services/README.md`.

## Certificates

No key material is committed or baked into the image. On first start the app generates a
CA at `Mitm:CertificatePath` — `/app/certs/mitm-ca.pfx` in the container — and writes the
public certificate beside it. The paths are printed in the startup log.

**Mount a volume at `/app/certs`.** Without one that directory lives in the container's
writable layer: it survives a restart of that same container, but any new container — a
redeploy, a new revision — mints a new CA, and every client then has to trust the new one.
Trusting a root CA is an operating-system step, not a browser click, so this is not
something to redo mid-demo:

```bash
docker run -v sitm-ca:/app/certs -p 3128:3128 -p 3127:3127 -p 8080:8080 -e Jwt__Key=<key> sitm-backend
```

An existing CA can be supplied instead with `Mitm:CertificatePath` and
`Mitm:CertificatePassword` (`Mitm__CertificatePath` / `Mitm__CertificatePassword` as
environment variables), ideally from a mounted secret rather than a file in the image.

The same CA signs:

- the per-host certificates used to intercept HTTPS, and
- the certificate on the `Proxy:HttpsPort` listener, covering `localhost`, the loopback
  addresses, the machine's own name and addresses, and anything in `Proxy:HostNames`.

A device that already trusts `/ca.crt` for interception therefore reaches the TLS proxy
port without a second warning.

Clients download it from **`/ca.crt`**; devices that prefer auto-configuration can use
**`/proxy.pac`**.

### Trusting it on a client

This is not a browser-level action for Chrome, Edge or Safari — they read the operating
system's root store, and Firefox keeps its own.

- **Windows**: import into `Trusted Root Certification Authorities`, then restart the
  browser. As Administrator:
  `Import-Certificate -FilePath .\mitm-ca.cer -CertStoreLocation Cert:\LocalMachine\Root`
- **Firefox**: Settings → Privacy & Security → Certificates → View Certificates →
  Authorities → Import, and tick "Trust this CA to identify websites".
- **iOS**: install the profile, *then* enable it separately under Settings → General →
  About → Certificate Trust Settings. Both steps are required.
- **Android**: Settings → Security → Encryption & credentials → Install a certificate →
  CA certificate. Note that since Android 7 only browsers honour user-installed CAs;
  other apps will not be intercepted.

Clicking through the browser's warning page instead is not a substitute: it is per-site,
and sites on the HSTS preload list (Google, GitHub, most banks) offer no bypass at all.

Remove the CA from every device when the demo is over.

## Trying it

```bash
docker build -f backend/Dockerfile -t sitm-backend backend
docker run --rm -v sitm-ca:/app/certs -p 3128:3128 -p 3127:3127 -p 8080:8080 -e Jwt__Key=<a-base64-key> sitm-backend
```

```bash
curl --ssl-no-revoke --proxy http://localhost:3128 http://example.com/
curl --ssl-no-revoke --proxy http://localhost:3128 https://example.com/
```

Swagger is on the API port, not the proxy port: <http://localhost:8080/swagger>.

`--ssl-no-revoke` is needed on Windows because schannel cannot check revocation for a
private CA. Elsewhere, pass the CA with `--cacert`.

## Scope

This decrypts HTTPS on the proxy and logs each transferred chunk. Only use it with clients
and traffic you are authorized to inspect.

The proxy is intentionally unauthenticated and unrestricted for proof-of-concept use. Do
not expose it to an untrusted network without adding authentication, destination
restrictions, connection limits, and protections against private or metadata addresses.
