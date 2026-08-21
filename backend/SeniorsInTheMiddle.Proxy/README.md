# SeniorsInTheMiddle Forward Proxy

This proof-of-concept exposes an open HTTP forward proxy on port `8080` in the container and `http://localhost:5284` when run with the development launch profile.

It supports:

- HTTP requests using absolute-form proxy URLs.
- HTTPS through HTTP/1.1 `CONNECT` tunnels.
- Arbitrary destination hosts and ports.

Examples:

```bash
curl --proxy http://localhost:5284 http://example.com/
curl --proxy http://localhost:5284 https://example.com/
```

This proxy is intentionally unauthenticated and unrestricted for proof-of-concept use. Do not expose it to an untrusted network without adding authentication, destination restrictions, connection limits, and protections against private or metadata addresses.
