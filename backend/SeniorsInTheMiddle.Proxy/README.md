# SeniorsInTheMiddle Forward Proxy

This proof-of-concept exposes an open HTTP forward proxy on port `8080` in the container and `http://localhost:5284` when run with the development launch profile.

It supports:

- HTTP requests using absolute-form proxy URLs.
- HTTPS interception through HTTP/1.1 `CONNECT` requests.
- Arbitrary destination hosts and ports.

Examples:

```bash
curl --ssl-no-revoke --proxy http://localhost:5284 http://example.com/
curl --ssl-no-revoke --proxy http://localhost:5284 https://example.com/
```

On first start, the proxy creates `mitm-ca.pfx` and the public `mitm-ca.cer` beside the
application. The exact path is printed in the startup log. Install and trust `mitm-ca.cer`
on the client device, then restart the browser.
On Windows, import it into `Trusted Root Certification Authorities`, or run as Administrator:

```powershell
Import-Certificate -FilePath .\mitm-ca.cer -CertStoreLocation Cert:\LocalMachine\Root
```

The CA can instead be provided with `Mitm:CertificatePath` and `Mitm:CertificatePassword`;
the public certificate path can be set with `Mitm:CertificatePublicPath`.

This decrypts HTTPS on the proxy and logs each transferred chunk as Base64. Only use this
with clients and traffic you are authorized to inspect.

This proxy is intentionally unauthenticated and unrestricted for proof-of-concept use. Do not expose it to an untrusted network without adding authentication, destination restrictions, connection limits, and protections against private or metadata addresses.
