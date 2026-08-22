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

| Port | Configured by      | Serves                                              |
|------|--------------------|-----------------------------------------------------|
| 8080 | `Proxy:HttpPort`   | Proxy traffic **and** the API and telemetry stream. |
| 8443 | `Proxy:HttpsPort`  | The same over TLS. Set to `0` to disable.           |

Under the `Development` launch profile these are `5284` and `5285`.

Proxy clients and the API share port 8080 because they are told apart by the request
line: a proxy client sends absolute form (`GET http://example.com/ HTTP/1.1`), everything
else arrives in origin form (`GET /api/v1/... `). An absolute-form request aimed at this
app's own address is answered locally rather than proxied, so the proxy never loops back
into itself.

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

A SignalR hub at `/hub/telemetry`, on both ports. It is one-way: no callable methods, and
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

What is wired today is only the start and the end of a forwarded plain-HTTP request
(`ForwardProxy.HandleAsync`). Everything else the protocol describes — treatment,
detection, redaction, the exchange lifecycle, anything at all about HTTPS traffic inside a
`CONNECT` tunnel — waits on the proxy pipeline. `RequestObserved.Treatment` is a
placeholder `passthrough` with the reason `not inspected` until then; deciding what a body
is belongs to the proxy, not to the code that reports on it.

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
docker run -v sitm-ca:/app/certs -p 8080:8080 -p 8443:8443 -e Jwt__Key=<key> sitm-backend
```

An existing CA can be supplied instead with `Mitm:CertificatePath` and
`Mitm:CertificatePassword` (`Mitm__CertificatePath` / `Mitm__CertificatePassword` as
environment variables), ideally from a mounted secret rather than a file in the image.

The same CA signs:

- the per-host certificates used to intercept HTTPS, and
- the certificate on the `Proxy:HttpsPort` listener, covering `localhost`, the loopback
  addresses, the machine's own name and addresses, and anything in `Proxy:HostNames`.

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
docker run --rm -v sitm-ca:/app/certs -p 8080:8080 -p 8443:8443 -e Jwt__Key=<a-base64-key> sitm-backend
```

```bash
curl --ssl-no-revoke --proxy http://localhost:8080 http://example.com/
curl --ssl-no-revoke --proxy http://localhost:8080 https://example.com/
```

`--ssl-no-revoke` is needed on Windows because schannel cannot check revocation for a
private CA. Elsewhere, pass the CA with `--cacert`.

## Scope

This decrypts HTTPS on the proxy and logs each transferred chunk. Only use it with clients
and traffic you are authorized to inspect.

The proxy is intentionally unauthenticated and unrestricted for proof-of-concept use. Do
not expose it to an untrusted network without adding authentication, destination
restrictions, connection limits, and protections against private or metadata addresses.
