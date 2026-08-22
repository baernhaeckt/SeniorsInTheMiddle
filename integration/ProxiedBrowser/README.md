# Demo Browser (macOS + Windows)

A small tabbed browser built with **.NET 10 / Avalonia / Chromium Embedded Framework (CefGlue)** that routes all
traffic through an explicitly configured proxy and trusts the proxy's CA certificate **in-process only** — nothing
is installed into the macOS keychain or the Windows certificate store, and no admin rights are needed.

It runs on **macOS and Windows from one code base** and is a feature-for-feature port of the WPF / WebView2 build
in `../ProxiedBrowser`. Same settings file schema, same startup sequence, same tab/address-bar behaviour, same
lock-icon and settings dialogs, same splash screen; only the UI toolkit and the browser engine differ. The table at
the end maps each WebView2 mechanism to its CEF counterpart.

Difference to the WebView2 build worth knowing before choosing one: this build **carries its own Chromium**
(~410 MB per platform) instead of using the Edge WebView2 runtime that is already on the machine, so the download
is much larger. In exchange it is one code base for both platforms and needs no runtime installed on the target
machine.

## Build & run

Prerequisites: the **.NET 10 SDK** on the machine that builds. Nothing has to be installed on the target machine —
the published output carries both the .NET runtime and Chromium. macOS 11+ or Windows 10/11.

```bash
dotnet build DemoBrowser.slnx
dotnet run --project DemoBrowser/DemoBrowser.csproj
```

Publishing (both write into the ignored `publish/` folder, and each has to run on the platform it targets):

```bash
./publish.sh          # macOS  → publish/Demo Browser.app   (~410 MB)
```

```powershell
.\publish.ps1            # Windows → publish\win-x64\DemoBrowser.exe   (~490 MB)
.\publish.ps1 -Compress  #           same, payload compressed          (~200 MB, slower first launch)
.\publish.ps1 -Zip       #           plus publish\DemoBrowser-win-x64.zip
```

**The Windows output is a single `DemoBrowser.exe` — that one file is the whole application.** CEF is loaded as a
native library (`libcef.dll`) that reads its resources (`icudtl.dat`, `*.pak`, `locales\`) from the directory it
lives in, and it starts `CefGlueBrowserProcess\Xilium.CefGlue.BrowserProcess.exe` as a real child process. That
works because `IncludeAllContentForSelfExtract` makes the apphost unpack the *entire* payload — Chromium's data
files included, not just the managed assemblies — into `%TEMP%\.net\DemoBrowser\<hash>\` and run from there, so
libcef and the helper process find each other exactly as they would in a normal folder layout. The extraction is
keyed by content hash: it happens on the first launch and is reused afterwards.

The plain build trades size for speed — the payload is stored uncompressed so the first launch only has to copy it
out. `-Compress` roughly halves the file at the cost of a noticeably slower first start.

The macOS bundle is ad-hoc signed; Gatekeeper still flags it as unsigned on first launch (right-click › Open, or
`xattr -dr com.apple.quarantine "publish/Demo Browser.app"`). It is an **x64** bundle — CefGlue ships CEF 120 for
macOS x64 only — and runs under Rosetta 2 on Apple Silicon (macOS offers to install Rosetta on first launch if it
is missing).

Keyboard: `⌘T` / `Ctrl+T` new tab, `⌘W` / `Ctrl+W` close tab, `⌘L` / `Ctrl+L` focus address bar, `F5` reload,
`F12` or `⌘⇧I` / `Ctrl+Shift+I` developer tools, `Ctrl+Shift+D` proxy diagnostics.

## Debugging the proxy

Behind the MITM proxy every HTTPS site fails Chromium's own certificate validation — the proxy re-signs each site
with its own CA — and the app is expected to override that in `CertificateService.HandleServerCertificateError`.
That makes a blank page ambiguous: the CA may never have loaded, the override may never have run, or it may have
run and rejected the certificate. `cef.log` cannot tell them apart, because Chromium logs its own verdict
(`ERR_CERT_AUTHORITY_INVALID`) in **all three** cases.

The **Proxy diagnostics** window (`Ctrl+Shift+D`, or the 🛠 button next to ⚙) shows which one it is:

* the switches the engine was actually started with, and whether the proxy is on at all;
* the CA that was downloaded — subject, thumbprint, validity, and whether it really is a CA;
* one line per certificate decision, with the chain that was built, or the `X509ChainStatus` flags explaining
  why a chain could not be built.

"Copy all" puts the whole report on the clipboard. The window is modeless, so a failing page can be reloaded while
it stays open.

### Developer tools

`F12` (or the `</>` button in the toolbar) opens Chromium's own DevTools for the active tab — the Network tab is
the quickest way to see what the proxy actually returned, and Security shows the re-signed certificate the way
Chromium sees it. Pressing `F12` again closes the window; it belongs to the tab it was opened from, so every tab
has its own.

DevTools is a **windowed** browser next to the off-screen rendered tab, opened via `CefBrowserHost.ShowDevTools`.
It deliberately does not use CefGlue's `AvaloniaCefBrowser.ShowDeveloperTools()`: that hands the tab's own
`CefClient` to CEF, and the off-screen adapter's `OnBrowserClose` — unlike the windowed one — does not check
`browser.IsPopup`, so closing the DevTools window tears down the *tab's* browser and leaves a dead tab behind.
`TabBrowser.ToggleDevTools` passes a separate, handler-free client instead, which keeps the DevTools browser off
the tab's adapter entirely.

Set `"UseProxy": false` in `settings.json` (or clear the checkbox in the settings dialog) to connect directly and
skip the CA download entirely. That separates "the proxy is broken" from "the browser is broken" in one step.

```bash
dotnet test          # 31 tests: proxy switches, CA download/parse, and the chain-to-proxy-CA decision
```

The trust tests build a throw-away CA and leaf certificates in memory, so they cover the decision that keeps pages
from rendering without needing a proxy to be running.

> The folder is named `ProxiedBrowser_osx` for historical reasons — it is the cross-platform build. Rename it
> (and this reference) if it becomes the only implementation.

## Files

All state lives under `~/Library/Application Support/DemoBrowser/` (macOS) resp. `%LOCALAPPDATA%\DemoBrowser\`
(Windows):

| Path | Purpose |
|------|---------|
| `settings.json` | Proxy, CA URL and start page (schema below). Created with defaults on first run. |
| `CEF/` | The Chromium profile: cookies, cache, history, local storage — shared by all tabs **within one run only**. It is deleted on every start and again on exit, so each launch is a clean profile with a single tab on `StartPage`. Nothing (tabs, history, cookies) is restored from a previous run. (CEF hosts the browser in-process, so parts of the profile stay memory-mapped until the process is gone; a detached `/bin/sh` waits for the PID to disappear and removes the folder. Even if that fails, the next start wipes it.) |
| `cef.log` | Chromium's own log (warnings and errors only). |

### settings.json schema

```json
{
  "UseProxy": true,
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
| `UseProxy` | `false` connects directly (`--no-proxy-server`) and skips the CA download; every other proxy key below is then ignored. Useful to tell a broken proxy apart from a broken browser. Default `true`. |
| `ProxyScheme` | `http` (plain HTTP CONNECT proxy, port 3128) or `https` (TLS-terminating proxy, port 3127). The settings dialog swaps the port automatically when you switch scheme. |
| `ProxyHost`, `ProxyPort` | Proxy endpoint. Port must be 1–65535. |
| `ProxyBypassList` | Chromium `--proxy-bypass-list` syntax, e.g. `localhost;*.corp.example.com`. Empty = nothing bypassed. |
| `CaCertUrl` | URL serving the proxy CA as PEM or DER. The proxy publishes it (and `proxy.pac`) on its plain-HTTP port: `http://<host>:3128/ca.cer`. An https URL is validated with full TLS. |
| `StartPage` | URL opened for new tabs and when there is no session to restore. |

The gear button opens a dialog that edits all of these with validation.

## How the proxy wiring works

`BrowserEnvironmentService` initialises the CEF runtime exactly **once** per process with the command-line switches

```
--proxy-server={ProxyScheme}://{ProxyHost}:{ProxyPort} [--proxy-bypass-list={ProxyBypassList}]
```

or, with `UseProxy: false`, the single switch `--no-proxy-server` (without it Chromium would quietly fall back to
the system proxy configuration, so "proxy off" would not mean "direct" on a machine that has one)

(passed as individual switches through `CefRuntimeLoader.Initialize(settings, flags)`; values unquoted — quotes
would be passed to Chromium verbatim) and `CachePath` pointing at the freshly wiped `CEF/` profile. Every tab's
`AvaloniaCefBrowser` is created in that one runtime, so every tab uses the proxy and shares one (per-run) session.

No `--ignore-certificate-errors` or other blanket TLS switch is used.

### Limitation: proxy changes require a restart

Chromium reads `--proxy-server` from the browser process command line, which CEF fixes when the runtime is
initialised. There is no API to change it on a running runtime. The settings dialog therefore saves the file and
tells you to restart; the running instance keeps its original proxy.

## How in-app CA trust works

1. **Download** – `CertificateService.DownloadAsync` fetches `CaCertUrl` with a plain `HttpClient`. For https
   URLs that means **normal, full TLS validation**: no custom validation callback is set, there is no
   bootstrap-trust problem. The default points at the proxy's own plain-HTTP port (`http://<host>:3128/ca.cer`);
   note that plain HTTP cannot protect the download against substitution on the path. Failure is non-fatal: a
   yellow banner is shown and the browser starts anyway (HTTPS sites will then show the normal Chromium error page).
2. **Parse** – PEM (`-----BEGIN CERTIFICATE-----`) via `X509Certificate2.CreateFromPem`, otherwise DER via
   `X509CertificateLoader.LoadCertificate` (the .NET 10 non-obsolete loaders). The certificate is held in memory only.
3. **Enforce** – every tab installs a `CefRequestHandler` (before the native browser is created, so the first
   navigation is covered) whose `OnCertificateError` (synchronous, no deferral):
   - converts the presented leaf (`CefX509Certificate.GetPemEncoded`) into an `X509Certificate2`,
   - builds an `X509Chain` with `TrustMode = CustomRootTrust`, the proxy CA as the only trusted root,
     `RevocationMode = NoCheck`, `VerificationFlags = IgnoreWrongUsage`,
   - also accepts the leaf being the proxy CA itself (thumbprint match),
   - continues the CEF callback and returns `true` ("allow") if the chain builds to the proxy CA, otherwise returns
     `false` so genuine certificate errors still produce the standard Chromium error page. With no CA loaded, the
     answer is always `false`.

   Only the leaf is read from CEF: CefGlue's binding of `GetPEMEncodedIssuerChain` wraps a native *array* of
   binary values in one managed proxy, and releasing it corrupts CEF's reference counts (the process then dies in
   a finalizer). Nothing is lost — the proxy signs each site's certificate directly with its CA, and the chain
   shown in the lock popup is the validated path out of `X509Chain.ChainElements`.

Because the proxy MITMs every HTTPS connection, essentially every site fires this callback and chains to the proxy
CA — that is the intended design. Nothing touches the macOS keychain or the Windows certificate store, and trust
ends when the process exits.

### Where the lock popup's details come from

The WebView2 build reads everything from the DevTools event `Security.visibleSecurityStateChanged`. **CEF never
emits the Security domain** — its DevTools plumbing works (other domains deliver events normally), but enabling
`Security` succeeds and then stays silent, and `Network.getCertificate` answers with an empty list. The same
information is therefore assembled from two other sources:

| Shown in the popup | Source |
|---|---|
| security state, TLS protocol, key exchange, cipher | CDP event `Network.responseReceived` of the main document (`securityState` + `securityDetails`), subscribed per tab through `CefBrowserHost.AddDevToolsMessageObserver` + `SendDevToolsMessage("Network.enable")` |
| certificate chain | the validated `X509Chain` built in `OnCertificateError` — behind the MITM proxy that callback fires for every HTTPS site, which is exactly when the chain matters |
| "not secure" reasons | `CefSslInfo.CertStatus` flags, mapped to readable text (the WebView2 build lists CDP's `securityStateIssueIds`) |

For a site whose certificate Chromium accepts on its own (i.e. one *not* going through the proxy), no certificate
error fires and the chain list stays empty; state, protocol and cipher are still shown.

## Architecture

| Type | Role |
|------|------|
| `SettingsService` | JSON persistence via a source-generated `JsonSerializerContext`. |
| `CertificateService` | Download, parse, hold the CA; shared `HandleServerCertificateError`. |
| `BrowserEnvironmentService` | One-time, guarded initialisation of the CEF runtime (proxy switches, wiped profile); shutdown on exit. |
| `TabBrowser` | `AvaloniaCefBrowser` subclass exposing the underlying `CefBrowser` (≈ `CoreWebView2`). |
| `TabViewModel` | Title / Source / IsActive / loading state, status text and the tab's own `TabBrowser`; CEF handlers for certificate errors, popups and DevTools events. |
| `MainViewModel` | Tab collection, active tab, toolbar commands, address resolution. |
| `MainWindow` | Header-only tab strip (`ItemsControl`) plus a single `Grid` hosting **all** browser controls simultaneously; switching tabs only toggles `IsVisible`. Controls are never re-parented, so the native browser survives tab switches. A status bar shows Chromium's link/loading status (CEF has no built-in overlay). |
| `SettingsWindow` | Settings editor with validation and the restart notice. |
| `CertificateInfoWindow` | Lock-icon popup with the TLS parameters and certificate chain. |
| `SplashWindow` | Animated startup screen (minimum one second). |
| `MessageDialog` | OK message box in the app's own chrome (Avalonia has no `MessageBox`). |

### WebView2 → CEF mapping

| Windows build (WebView2) | macOS build (CefGlue) |
|---|---|
| `CoreWebView2Environment` + `AdditionalBrowserArguments` | `CefRuntimeLoader.Initialize(CefSettings, flags)` |
| Evergreen runtime check (`GetAvailableBrowserVersionString`) | bundled `libcef` + helper check (`CefRuntime.Load`, `ChromeVersion`) |
| `UserDataFolder` | `CefSettings.CachePath` / `RootCachePath` |
| `ServerCertificateErrorDetected` → `AlwaysAllow` / `Default` | `CefRequestHandler.OnCertificateError` → `callback.Continue()` + `true` / `false` |
| `NewWindowRequested` (`Handled = true`) | `CefLifeSpanHandler.OnBeforePopup` (returns `true`) |
| `DocumentTitleChanged`, `SourceChanged`, `HistoryChanged`, `NavigationStarting/Completed` | `TitleChanged`, `AddressChanged`, `LoadingStateChange`, `LoadStart`/`LoadEnd` |
| `Settings.IsStatusBarEnabled` | `StatusMessage` event → status bar row |
| `GetDevToolsProtocolEventReceiver` + `CallDevToolsProtocolMethodAsync` | `AddDevToolsMessageObserver` + `SendDevToolsMessage` |
| `CoreWebView2.OpenDevToolsWindow()` | `CefBrowserHost.ShowDevTools` / `CloseDevTools` / `HasDevTools` with a dedicated `CefClient` |
| `Security.visibleSecurityStateChanged` (TLS details + chain) | `Network.responseReceived` (TLS details) + `OnCertificateError` (chain) — CEF emits no Security domain |
| browser process wait + profile wipe on exit | wait for every browser's `OnBeforeClose`, then `CefRuntime.Shutdown()` + profile wipe (finished by a detached helper) |
| `Visibility.Collapsed` per inactive tab | `IsVisible = false` per inactive tab |
