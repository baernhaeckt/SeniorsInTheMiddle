# services

Python services that the dotnet backend talks to over unix sockets. Every
service gets its own socket, and both processes run in the same container.

| directory         | what it is                                                                 |
| ----------------- | -------------------------------------------------------------------------- |
| `service_runtime` | the shared package: socket, protocol, lifecycle, errors                     |
| `example_service` | reference service: implement `Service`, call `run()`, done                  |
| `test-host`       | dotnet console app that calls the example service and asserts the contract  |

The packages are plain directories, not installed distributions. They have no
dependencies of their own; python finds them because `services` is on the path
(`PYTHONPATH` in the container, the working directory when you run a service
from here).

## Implementing a service

```python
from typing import Any

from service_runtime import MethodNotFoundError, Service, run


class EchoService(Service):
    async def start(self) -> None:      # optional
        ...

    async def stop(self) -> None:       # optional
        ...

    async def handle(self, method: str, payload: dict[str, Any]) -> Any:
        if method == "echo":
            return payload
        raise MethodNotFoundError(method)


run(EchoService(), socket_path="/run/services/echo.sock")
```

`handle` returns anything JSON-serialisable; that value becomes the `result` of
the response. See `example_service` for a complete service.

## Configuration

Each service owns its socket. You set the path in code (`socket_path=`) or via
`SERVICE_SOCKET_PATH`. Everything else has a default:

| `RuntimeConfig` field     | env (`SERVICE_` prefix)   | default | meaning                                          |
| ------------------------- | ------------------------- | ------- | ------------------------------------------------ |
| `socket_path`             | `SOCKET_PATH`             | -       | unix socket to listen on (required)              |
| `socket_mode`             | `SOCKET_MODE`             | `0o660` | chmod of the socket file, empty string = umask   |
| `max_frame_bytes`         | `MAX_FRAME_BYTES`         | 8 MiB   | largest accepted request frame                   |
| `request_timeout`         | `REQUEST_TIMEOUT`         | `30.0`  | seconds one `handle` call may take               |
| `max_concurrent_requests` | `MAX_CONCURRENT_REQUESTS` | `64`    | in-flight `handle` calls                         |
| `shutdown_timeout`        | `SHUTDOWN_TIMEOUT`        | `10.0`  | drain time for in-flight requests on SIGTERM     |
| `log_level`               | `LOG_LEVEL`               | `INFO`  | level of the `service_runtime` logger            |

```python
from service_runtime import RuntimeConfig, ServiceRuntime, run

run(MyService(), RuntimeConfig(socket_path="/run/services/my.sock", request_timeout=None))

# or drive the runtime yourself (tests, embedding in another event loop)
runtime = ServiceRuntime(MyService(), RuntimeConfig(socket_path="/tmp/my.sock"))
await runtime.start()
...
await runtime.stop()
```

## Wire protocol (`length-prefixed-json/1`)

Each message is a big-endian `uint32` byte length followed by that many bytes of
UTF-8 JSON:

```
+---------------------------+--------------------------+
| uint32 BE length          | UTF-8 JSON               |
+---------------------------+--------------------------+
```

Request:

```json
{ "id": "17", "method": "greet", "payload": { "name": "Bern" } }
```

Response, always exactly one per request, correlated by `id`:

```json
{ "id": "17", "ok": true,  "result": { "message": "Hoi, Bern!" } }
{ "id": "17", "ok": false, "error": { "code": "invalid_request", "message": "...", "details": { } } }
```

The runtime handles the requests on one connection concurrently, so responses
may come back in a different order than they were sent. It also accepts several
connections at the same time.

### Error codes

| code               | raised by                                                  |
| ------------------ | ---------------------------------------------------------- |
| `method_not_found` | `MethodNotFoundError` (services raise it for unknown verbs) |
| `invalid_request`  | `InvalidRequestError` and malformed envelopes               |
| `timeout`          | `handle` exceeded `request_timeout`                         |
| `cancelled`        | request cancelled because the runtime shut down             |
| `internal_error`   | any other exception escaping `handle`                       |
| anything else      | `ServiceError(..., code="your_code")`                       |

Frames that cannot be correlated to a request (bad JSON, missing `id`,
oversized frame) close the connection instead of answering.

### Built-in methods

The runtime answers these itself, so `$` is reserved and must not be used for
service methods:

- `$ping` - `{"pong": true, "service": "..."}`
- `$health` - status, uptime, in-flight requests, open connections
- `$info` - socket path, protocol version, limits

### Clients

`ServiceClient` is the python client, for calls between python services and for
poking at a service by hand. The dotnet side has an equivalent implementation in
[`test-host/ServiceSocketClient.cs`](test-host/ServiceSocketClient.cs); the proxy
carries a copy under `backend/src/SeniorsInTheMiddle.Proxy/Services/` with a
reconnecting `ServiceConnection` per service on top.

```python
from service_runtime import ServiceClient

async with ServiceClient("/run/services/echo.sock") as client:
    print(await client.call("echo", {"message": "hoi"}))
```

## Running the integration check

Unix sockets do not exist on windows, so the whole thing runs in one container:

```bash
docker build -t services-integration ./services
docker run --rm services-integration
```

or

```bash
docker compose -f services/docker-compose.yml up --build --exit-code-from integration
```

The container starts `example_service` on `/run/services/example-service.sock`,
lets the dotnet test host call it over that socket and finally checks that
SIGTERM shuts the service down cleanly. It exits non-zero if anything fails.


## Running in the backend image

In the deployment the services run next to the dotnet proxy in the backend
image (`backend/Dockerfile`, built from the repository root), supervised by
supervisord (`backend/supervisord.conf`). Each service is one `[program:..]`
block with its own socket under `/run/services/`, and the proxy is told where
to find it with `Services__<Name>__SocketPath`. The uv lock at the repo root
provides the python dependencies; spaCy models are downloaded at build time
(`SPACY_MODELS`, default `de_core_news_lg en_core_web_lg`) so nothing is
fetched at runtime.

```bash
docker build -f backend/Dockerfile -t sitm-proxy .
docker run --rm -p 8080:8080 -e Jwt__Key=... sitm-proxy
curl http://localhost:8080/healthz        # pings every configured service
docker exec <id> supervisorctl status      # pii-service, proxy
```

## Adding a new service

1. Copy `example_service`, rename the directory. Use relative imports inside
   the package and `from service_runtime import ...` for the runtime; the
   package root is `services/` (`PYTHONPATH=/srv/services` in the image).
2. Implement `handle` (and `start`/`stop` if you need them). Load models in
   `start`, not per request.
3. Pick a socket path, e.g. `/run/services/<name>.sock`, and add a
   `__main__.py` so `python -m <package>` starts it.
4. Wire it into the backend image:
   - `COPY services/<package> /srv/services/<package>` in `backend/Dockerfile`,
     plus a `<NAME>_SOCKET_PATH` / `Services__<Name>__SocketPath` env pair;
   - a `[program:<name>]` block in `backend/supervisord.conf`;
   - on the dotnet side a name in `ServiceConnections.KnownServices` and a typed
     client next to `Services/Pii/` (`backend/src/SeniorsInTheMiddle.Proxy/Services`).
