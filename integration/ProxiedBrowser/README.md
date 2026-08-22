# Demo Browser

A small tabbed Windows browser built with **.NET 10 / WPF / WebView2** that routes all traffic through an
explicitly configured proxy and trusts the proxy's CA certificate **in-process only** — nothing is installed
into the Windows certificate store and no admin rights are needed.

## Build & run

Prerequisites:

- .NET 10 SDK
- Microsoft Edge WebView2 Evergreen Runtime (pre-installed on Windows 11; otherwise
  https://developer.microsoft.com/microsoft-edge/webview2/). The app checks for it at startup and exits with a
  pointer to that page if it is missing.

```powershell
dotnet build DemoBrowser.slnx
dotnet run --project DemoBrowser\DemoBrowser.csproj
```

To produce a single self-contained `win-x64` executable (the ignored `publish\` folder):

```powershell
.\publish.ps1
```

Keyboard: `Ctrl+T` new tab, `Ctrl+W` close tab, `Ctrl+L` focus address bar, `F5` reload.

## Files

All state lives under `%LOCALAPPDATA%\DemoBrowser\`:

| Path | Purpose |
|------|---------|
| `settings.json` | Proxy, CA URL and start page (schema below). Created with defaults on first run. |
| `WebView2\` | The WebView2 user data folder: cookies, cache, history, local storage — shared by all tabs **within one run only**. It is deleted on every start and again on exit, so each launch is a clean profile with a single tab on `StartPage`. Nothing (tabs, history, cookies) is restored from a previous run. |

### settings.json schema

```json
{
  "ProxyScheme": "http",
  "ProxyHost": "seniorsinthemiddle-backend.icymushroom-561b0fa4.northeurope.azurecontainerapps.io",
  "ProxyPort": 3128,
  "ProxyBypassList": "",
  "CaCertUrl": "http://seniorsinthemiddle-backend.icymushroom-561b0fa4.northeurope.azurecontainerapps.io:3128/ca.cer",
  "StartPage": "https://example.com"
}
```

| Key | Meaning |
|-----|---------|
| `ProxyScheme` | `http` (plain HTTP CONNECT proxy, port 3128) or `https` (TLS-terminating proxy, port 3127). The settings dialog swaps the port automatically when you switch scheme. |
| `ProxyHost`, `ProxyPort` | Proxy endpoint. Port must be 1–65535. |
| `ProxyBypassList` | Chromium `--proxy-bypass-list` syntax, e.g. `localhost;*.corp.example.com`. Empty = nothing bypassed. |
| `CaCertUrl` | URL serving the proxy CA as PEM or DER. The proxy publishes it (and `proxy.pac`) on its plain-HTTP port: `http://<host>:3128/ca.cer`. An https URL is validated with full TLS. |
| `StartPage` | URL opened for new tabs and when there is no session to restore. |

The gear button opens a dialog that edits all of these with validation.

## How the proxy wiring works

`BrowserEnvironmentService` creates exactly **one** `CoreWebView2Environment` at startup with

```
--proxy-server={ProxyScheme}://{ProxyHost}:{ProxyPort} [--proxy-bypass-list={ProxyBypassList}]
```

in `CoreWebView2EnvironmentOptions.AdditionalBrowserArguments` (values unquoted — the string is whitespace-split
and quotes would be passed to Chromium verbatim). Every tab calls `EnsureCoreWebView2Async(environment)` with
that same instance, so every tab uses the proxy and shares one (per-run) session.

No `--ignore-certificate-errors` or other blanket TLS switch is used.

### Limitation: proxy changes require a restart

Chromium reads `--proxy-server` from the browser process command line, which WebView2 launches when the
environment is created. There is no API to change it on a running environment. The settings dialog therefore
saves the file and tells you to restart; the running instance keeps its original proxy.

## How in-app CA trust works

1. **Download** – `CertificateService.DownloadAsync` fetches `CaCertUrl` with a plain `HttpClient`. For https
   URLs that means **normal, full TLS validation**: no custom validation callback is set, there is no
   bootstrap-trust problem. The default points at the proxy's own plain-HTTP port (`http://<host>:3128/ca.cer`);
   note that plain HTTP cannot protect the download against substitution on the path. Failure is non-fatal: a
   yellow banner is shown and the browser starts anyway (HTTPS sites will then show the normal Edge interstitial).
2. **Parse** – PEM (`-----BEGIN CERTIFICATE-----`) via `X509Certificate2.CreateFromPem`, otherwise DER via
   `X509CertificateLoader.LoadCertificate` (the .NET 10 non-obsolete loaders). The certificate is held in memory only.
3. **Enforce** – every tab subscribes to `CoreWebView2.ServerCertificateErrorDetected` inside
   `CoreWebView2InitializationCompleted`, before its first navigation. The handler (synchronous, no deferral):
   - converts `e.ServerCertificate.ToPemEncoding()` and `PemEncodedIssuerCertificateChain` into `X509Certificate2`s,
   - builds an `X509Chain` with `TrustMode = CustomRootTrust`, the proxy CA as the only trusted root, the
     supplied issuers in `ExtraStore`, `RevocationMode = NoCheck`, `VerificationFlags = IgnoreWrongUsage`,
   - also accepts the leaf being the proxy CA itself (thumbprint match),
   - sets `Action = AlwaysAllow` if the chain builds to the proxy CA, otherwise `Default` so genuine certificate
     errors still produce the standard Edge interstitial. With no CA loaded, the action is always `Default`.

Because the proxy MITMs every HTTPS connection, essentially every site fires this event and chains to the proxy
CA — that is the intended design. Nothing touches the Windows certificate store, and trust ends when the
process exits.

## Architecture

| Type | Role |
|------|------|
| `SettingsService` | JSON persistence via a source-generated `JsonSerializerContext`. |
| `CertificateService` | Download, parse, hold the CA; shared `HandleServerCertificateError`. |
| `BrowserEnvironmentService` | One-time, guarded creation of the shared `CoreWebView2Environment`. |
| `TabViewModel` | Title / Source / IsActive / loading state and the tab's own `WebView2` instance. |
| `MainViewModel` | Tab collection, active tab, toolbar commands, address resolution. |
| `MainWindow` | Header-only tab strip (`ItemsControl`) plus a single `Grid` hosting **all** WebView2 controls simultaneously; switching tabs only toggles `Visibility`. WebView2 controls are never re-parented, so the CoreWebView2 survives tab switches. |
| `SettingsWindow` | Settings editor with validation and the restart notice. |
