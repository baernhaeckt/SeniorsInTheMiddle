# Seniors in the Middle: frontend

A wall dashboard for the PII boundary proxy, a transparent HTTP/HTTPS proxy that
sits between a household's devices and whatever they talk to. Most of what
crosses it is stylesheets, scripts, fonts and images, and it goes straight
through. A small part carries a body worth reading, usually JSON. That part gets
opened, stripped of anything identifying, forwarded, and put back together on the
way home.

The dashboard shows both: the whole stream in the traffic list, and the treated
requests step by step on the band above it.

## What this app does, and what it leaves to the backend

This app is only a view. It runs no detection, no redaction, no rehydration and
no classification, and it holds no policy. All of that happens in the proxy
backend, including the decision about which requests need treatment. The
dashboard subscribes to one WebSocket, receives events describing what the proxy
already did, and draws them.

The wire protocol in [`src/protocol/types.ts`](src/protocol/types.ts) is the
contract between the two sides.

## Running it

```bash
npm install     # resolves against registry.npmjs.org (see .npmrc)
npm run dev     # http://localhost:5173
npm run build   # static bundle into dist/
```

## Attaching the backend

Set the endpoint and the dashboard uses it for everything:

```bash
VITE_PROXY_WS_URL=ws://localhost:5080/stream npm run dev
```

The socket reconnects on its own with backoff, and the header badge reports the
real state (`live`, `reattaching`, `detached`) with the endpoint spelled out. The
dashboard drops any frame that does not parse as a protocol event instead of
guessing what it meant.

With no endpoint configured the app falls back to a built-in demo feed that
replays canned events in the same protocol, so you can show the visualization
before the backend exists. The header labels it `demo feed · no backend`, so
nobody mistakes it for real traffic. To force either source, add `?source=demo`
or `?source=ws` to the URL.

## The setup guide

The header has a button that opens a guide for putting a device behind the
proxy: join the network, trust the certificate, then check the traffic list.
The address it shows comes from `.env`, so change it there rather than in the
code.

```bash
VITE_PROXY_HOST=proxy.sitm.local
VITE_PROXY_PORT=8888
VITE_PROXY_NETWORK=SITM-Guest    # the Wi-Fi that already routes through the proxy
VITE_PROXY_CA_URL=               # optional, defaults to http://HOST:PORT/ca.crt
VITE_PROXY_PAC_URL=              # optional, defaults to http://HOST:PORT/proxy.pac
```

The guide has install steps for iPhone, Android, Windows and macOS, and a copy
button on every address. Leave `VITE_PROXY_CA_URL` and `VITE_PROXY_PAC_URL`
empty unless the proxy serves those files from somewhere else.

## The protocol

Every request through the proxy produces two events:

| Event | Meaning |
| --- | --- |
| `request.observed` | a request was seen, and the proxy has decided how to handle it |
| `request.completed` | it finished; status, size, duration |

`request.observed` carries a `treatment`. That value is the marking the traffic
list shows, and it alone decides whether a request reaches the band:

| Treatment | Meaning |
| --- | --- |
| `passthrough` | non-sensitive by type: CSS, scripts, fonts, images. The body is never read. |
| `clean` | the body was read, nothing identifying in it |
| `treated` | identifiers found and replaced before the request left |

A `treated` request also produces the full lifecycle between those two events.
Each of these moves its packet on the band to the next position:

| Event | Meaning |
| --- | --- |
| `exchange.opened` | method, scheme, host, path, content type, raw request body |
| `detection.completed` | identifiers found, with kind, offsets, token and confidence |
| `redaction.completed` | the body with every identifier swapped for its token |
| `upstream.dispatched` | tokenized body sent on to the destination |
| `upstream.responded` | the destination's response, still tokenized |
| `rehydration.completed` | tokens swapped back, for the client's eyes only |
| `exchange.delivered` | round trip closed, with the latency the proxy measured |

`hello` announces the proxy. `log` can arrive at any time, and the newest line
sits under the traffic list.

The proxy decides everything. If it never sends `detection.completed`, the
dashboard shows nothing found. It does not scan the body itself, and it does not
decide what counts as an asset.

## Reading the screen

Colour means one thing here and nothing else:

- amber: data that identifies a person
- cyan: data that does not
- rose: an identifier caught mid-flight, about to be held back

### The band

The glowing line is the boundary. Everything left of it can identify a person,
and nothing right of it can. Faint motes stream straight across the middle
without stopping, which is the untreated traffic that never gets opened. Treated
requests ride the upper rail, pause at the gate while the proxy reads them,
disappear inside, and come out the other side carrying tokens. Restored responses
come back on the lower rail.

### The traffic list

Every request, newest first, with its marking on the right. Click any treated row
to hold it in the inspector, and `follow live` releases it again.

### The payload inspector

One treated exchange as a matrix: what the client sent and what the destination
saw, outbound and inbound. JSON bodies are laid out for reading when they parse.
Hovering an identifier highlights it in every panel at once.

### The vault

The token-to-value table. Values stay blurred until you hover them, because that
table is the one place the real data still exists.

## Deployment

The Dockerfile builds the bundle and serves it from nginx on port 8080, which is
what `.github/workflows/frontend.yml` deploys to Azure Container Apps. To bake in
the backend endpoint at build time:

```bash
docker build --build-arg VITE_PROXY_WS_URL=wss://proxy.example.ch/stream -t sitm-frontend .
```

## Notes for the demo

The band runs slower than real traffic so a room can follow it. A treated request
takes about nine seconds to cross and come back. The latency in the header is the
figure the proxy reports, not the animation's wall clock.

The sample traffic in [`src/demo/scenarios.ts`](src/demo/scenarios.ts) is a
plausible hour from one household: an insurance claim, a bank transfer draft, a
municipal address change, a triage question, and an assistant conversation about
a grandchild scam, surrounded by the assets those apps pull down. Every name, AHV
number, IBAN, address and host in it is invented.
