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
dashboard subscribes to one SignalR hub, receives events describing what the
proxy already did, and draws them.

The wire protocol in [`src/protocol/types.ts`](src/protocol/types.ts) is the
contract between the two sides.

## Running it

```bash
npm install     # resolves against registry.npmjs.org (see .npmrc)
npm run dev     # http://localhost:5173
npm run build   # static bundle into dist/
```

## Working on it

```bash
npm run check          # everything CI runs: lint, format, types, dead code, tests
npm run lint           # eslint (type-aware, react-hooks, jsx-a11y) + stylelint
npm run format         # prettier --write
npm run typecheck      # tsc -b, strict with noUncheckedIndexedAccess
npm run knip           # unused files, exports and dependencies
npm test               # vitest, once
npm run test:watch
npm run test:coverage
```

The `quality` job in `.github/workflows/frontend.yml` runs `check` and `build`
on every pull request that touches `frontend/`; the deploy job waits for it.

Layout:

| Path              | What lives there                                                                                |
| ----------------- | ----------------------------------------------------------------------------------------------- |
| `src/protocol/`   | the wire contract: valibot schemas, the types derived from them, recorded frames in `fixtures/` |
| `src/transport/`  | where events come from: the SignalR client and the demo feed                                    |
| `src/engine/`     | the store and pure helpers: reducer, selectors, geometry, text                                  |
| `src/components/` | React views; each subscribes to the slice of the store it draws via `useStore`                  |
| `src/styles/`     | one CSS file per area, plus `tokens.css` and `base.css`                                         |
| `src/ui/`         | small shared hooks and helpers                                                                  |

Pure modules have unit tests next to them (`*.test.ts`); components have
`*.test.tsx` with Testing Library. `src/protocol/fixtures/` holds recorded
event sequences that must parse and reduce end to end; add a file there when
the proxy starts emitting something new.

Fonts ship with the bundle (`@fontsource`), so the dashboard makes no request
to anything but the telemetry socket.

## Configuration

Nothing is baked in at build time, so the same bundle points at any proxy. On
first run the app opens a setup screen and asks for:

| Field                   | Meaning                                                            |
| ----------------------- | ------------------------------------------------------------------ |
| Telemetry stream        | live proxy, or the built-in demo feed                              |
| Telemetry hub URL       | where to read events from, when the source is the live proxy       |
| Host and port           | what the setup guide tells people to type into a device            |
| Wi-Fi name              | the network that already routes through the proxy, if there is one |
| Certificate and PAC URL | optional, derived from host and port when left empty               |

The form starts filled in for a proxy on this machine — hub
`http://localhost:8080/hub/telemetry`, host `localhost`, port `3128` — which is what
`docker compose up` in `integration/` runs, so a fresh install reaches a local proxy
without anything being typed.

The two ports differ on purpose: the backend serves the telemetry hub on its API port
(`8080`) and takes proxy traffic on `3128`, so the certificate and PAC URLs derived from
host and port point at `3128` while the hub URL does not.

The values are saved in `localStorage` under `sitm.config.v3` and belong to that
browser. Reconfigure in the header reopens the form with the current values, and
Cancel goes back without changing anything. Saving resets the dashboard so
traffic from the old source does not sit there looking live.

A saved config that fails validation counts as absent, which sends you back to
the setup screen rather than half-configured into the dashboard.

### The demo feed

Pick Demo feed to run without a backend. It replays canned events in the same
protocol, and the header labels it `demo feed · no backend` so nobody mistakes
it for real traffic.

### The live proxy

Pick Live proxy and give it the hub's `http://` or `https://` address. It is an
HTTP URL even though it ends up as a socket: SignalR is handed the address and
converts it itself.

Negotiation is skipped and the transport pinned to WebSockets, so the browser
opens the socket directly rather than posting to `/negotiate` first. That keeps
the page's `connect-src` policy to `ws:`/`wss:` and takes CORS out of the
picture — which matters, because the proxy's address is typed in at runtime and
cannot be baked into a Content-Security-Policy at build time. The proxy checks
the handshake's `Origin` against its own allowlist instead, so the dashboard's
origin has to appear in the proxy's `Cors:AllowedOrigins`.

Reconnection is the transport's own rather than SignalR's `withAutomaticReconnect`,
which only covers a connection that was established once and does nothing for a
proxy that is not up yet — the usual case when someone opens the dashboard
first. It retries with jittered backoff, and the header badge reports the real
state (`live`, `reattaching`, `detached`) with the endpoint spelled out.

Every frame is validated against the schemas in `src/protocol/types.ts`. A
frame that does not validate, including one with an unknown `type`, is dropped
and counted; the badge shows the count and the first rejection of each kind is
logged to the console. If the proxy's `hello` announces a protocol version
other than the one this view was built for, the badge says so.

## The setup guide

The header has a button showing the proxy address. It opens a guide for putting
a device behind the proxy: join the network, trust the certificate, then check
the traffic list. There are install steps for iPhone, Android, Windows and
macOS, and a copy button on every address. All of it reads from what you entered
on the setup screen.

## The protocol

Every request through the proxy produces two events:

| Event               | Meaning                                                        |
| ------------------- | -------------------------------------------------------------- |
| `request.observed`  | a request was seen, and the proxy has decided how to handle it |
| `request.completed` | it finished; status, size, duration                            |

`request.observed` carries a `treatment`. That value is the marking the traffic
list shows, and it alone decides whether a request reaches the band:

| Treatment     | Meaning                                                                     |
| ------------- | --------------------------------------------------------------------------- |
| `passthrough` | non-sensitive by type: CSS, scripts, fonts, images. The body is never read. |
| `clean`       | the body was read, nothing identifying in it                                |
| `treated`     | identifiers found and replaced before the request left                      |

A `treated` request also produces the full lifecycle between those two events.
Each of these moves its packet on the band to the next position:

| Event                   | Meaning                                                     |
| ----------------------- | ----------------------------------------------------------- |
| `exchange.opened`       | method, scheme, host, path, content type, raw request body  |
| `detection.completed`   | identifiers found, with kind, offsets, token and confidence |
| `redaction.completed`   | the body with every identifier swapped for its token        |
| `upstream.dispatched`   | tokenized body sent on to the destination                   |
| `upstream.responded`    | the destination's response, still tokenized                 |
| `rehydration.completed` | tokens swapped back, for the client's eyes only             |
| `exchange.delivered`    | round trip closed, with the latency the proxy measured      |

An entity's `kind` is the detector's own name for the category (`PERSON`,
`EMAIL_ADDRESS`, ...), shown as it arrives.

`hello` announces the proxy. `log` can arrive at any time, and the newest line
sits under the traffic list.

The proxy decides everything. If it never sends `detection.completed`, the
dashboard shows nothing found. It does not scan the body itself, and it does not
decide what counts as an asset.

Two transitions on the band are the view's own, because the proxy has no idea
where a packet is drawn: after `upstream.dispatched` the request sits in the
`egress` stage until its packet has left the gate, then becomes `thinking`;
after `exchange.delivered` the response lingers at the client and then leaves
the band. Both go through `store.settle`.

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

One treated exchange, outbound and inbound. What the client sent and what the
destination saw are stacked over each other rather than set side by side, and
the ruler between them wipes from one to the other: drag it to read a name
turning into its token where the name stood, or press it to jump to one side
whole. Each row is one scroller holding both readings, so they cannot drift out
of step. JSON bodies are laid out for reading when they parse. Hovering an
identifier highlights it in every panel at once.

### The vault

The token-to-value table. Values stay blurred until you hover them, because that
table is the one place the real data still exists.

### Projector mode

The `AA` button in the header switches the dashboard to a size a room can read.
A FullHD projector has a laptop's pixel count and none of its viewing distance,
so this is not a zoom: the type collapses from fifteen sizes onto four that all
start at 13px, and the desk detail pays for the room it needs — timestamps, byte
counts, the log ticker, the near misses, the proxy address and the kind chips
beside each held value all step out.

The choice is remembered in `localStorage` under `sitm.projector.v1` and the
button toggles it back. It is one attribute on the root element, so everything
the mode does lives in [`src/styles/projector.css`](src/styles/projector.css) —
with the mode off, none of those rules match.

## Deployment

The Dockerfile builds the bundle and serves it from nginx on port 8080, which is
what `.github/workflows/frontend.yml` deploys to Azure Container Apps.

```bash
docker build -t sitm-frontend .
```

There are no build arguments. Whoever opens the dashboard configures it in the
browser.

## Notes for the demo

The band runs slower than real traffic so a room can follow it. A treated request
takes about nine seconds to cross and come back. The latency in the header is the
figure the proxy reports, not the animation's wall clock.

The sample traffic in [`src/demo/scenarios.ts`](src/demo/scenarios.ts) is a
plausible hour from one household: an insurance claim, a bank transfer draft, a
municipal address change, a triage question, and an assistant conversation about
a grandchild scam, surrounded by the assets those apps pull down. Every name, AHV
number, IBAN, address and host in it is invented.
