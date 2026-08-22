import type { PlainRequest } from '../demo/scenarios'
import type { ServerEvent } from '../protocol/types'
import { CLEAN, PASSTHROUGH, TREATED, compileExchange } from '../demo/scenarios'
import { createEmitter, type LinkStatus, type Transport } from './types'

const ENDPOINT = 'demo feed · no backend'

/**
 * Stand-in for the proxy until the backend ships its stream.
 *
 * It speaks the exact protocol in `src/protocol/types.ts` and nothing else, so
 * choosing the live proxy on the setup screen swaps this out with no change to
 * the view. Assets stream through constantly; a request worth treating turns up
 * every few seconds, the way it would on a real line.
 */
export function createDemoTransport(): Transport {
  const events = createEmitter<ServerEvent>()
  const status = createEmitter<LinkStatus>()
  const timers = new Set<number>()

  let running = false
  let requestSeq = 0
  let exchangeSeq = 0
  let last: LinkStatus = { state: 'idle', endpoint: ENDPOINT }

  const after = (ms: number, run: () => void) => {
    const id = window.setTimeout(() => {
      timers.delete(id)
      if (running) run()
    }, ms)
    timers.add(id)
  }

  const jitter = (base: number, spread: number) => base + Math.random() * spread
  const pick = <T>(items: readonly T[]): T => {
    const item = items[Math.floor(Math.random() * items.length)]
    if (item === undefined) throw new Error('demo sample list is empty')
    return item
  }
  const nextRequestId = () => `r-${String((requestSeq += 1)).padStart(5, '0')}`

  /** An asset or an uninteresting body: observed, completed, done. */
  const runPlain = (sample: PlainRequest, treatment: 'passthrough' | 'clean') => {
    const requestId = nextRequestId()
    const durationMs = Math.round(jitter(18, treatment === 'clean' ? 180 : 90))

    events.emit({
      type: 'request.observed',
      requestId,
      at: Date.now(),
      clientIp: sample.clientIp,
      clientLabel: sample.clientLabel,
      method: sample.method,
      scheme: 'https',
      host: sample.host,
      path: sample.path,
      contentType: sample.contentType,
      requestBytes: sample.bytes,
      treatment,
      reason: sample.reason,
    })

    after(durationMs, () => {
      events.emit({
        type: 'request.completed',
        requestId,
        at: Date.now(),
        status: sample.status,
        responseBytes: sample.responseBytes,
        durationMs,
      })
    })
  }

  /** A request with something in it: the full lifecycle, slowed to be watchable. */
  const runTreated = () => {
    const scenario = TREATED[exchangeSeq % TREATED.length] ?? pick(TREATED)
    const compiled = compileExchange(scenario, exchangeSeq)
    const exchangeId = `x-${String(exchangeSeq).padStart(4, '0')}`
    const requestId = nextRequestId()
    exchangeSeq += 1

    const scannedMs = Math.round(jitter(9, 14))
    const upstreamMs = Math.round(jitter(120, 380))
    const target = `${scenario.host}${scenario.path}`

    events.emit({
      type: 'request.observed',
      requestId,
      at: Date.now(),
      clientIp: scenario.clientIp,
      clientLabel: scenario.clientLabel,
      method: scenario.method,
      scheme: 'https',
      host: scenario.host,
      path: scenario.path,
      contentType: scenario.contentType,
      requestBytes: compiled.requestBody.length,
      treatment: 'treated',
      reason: `${compiled.entities.length} identifiers`,
      exchangeId,
    })

    events.emit({
      type: 'exchange.opened',
      exchangeId,
      requestId,
      at: Date.now(),
      clientLabel: scenario.clientLabel,
      method: scenario.method,
      scheme: 'https',
      host: scenario.host,
      path: scenario.path,
      contentType: scenario.contentType,
      requestBody: compiled.requestBody,
    })

    after(jitter(1500, 400), () => {
      events.emit({
        type: 'detection.completed',
        exchangeId,
        at: Date.now(),
        entities: compiled.entities,
        scannedMs,
      })
    })

    after(jitter(2600, 400), () => {
      events.emit({
        type: 'redaction.completed',
        exchangeId,
        at: Date.now(),
        redactedRequestBody: compiled.redactedRequestBody,
      })
      events.emit({
        type: 'log',
        at: Date.now(),
        level: 'block',
        exchangeId,
        message: `held at the boundary: ${summarize(compiled.entities.map((e) => e.kind))}`,
      })
    })

    after(jitter(4200, 300), () => {
      events.emit({
        type: 'upstream.dispatched',
        exchangeId,
        at: Date.now(),
        target,
        bytes: compiled.redactedRequestBody.length,
      })
    })

    after(jitter(5800, 500), () => {
      events.emit({
        type: 'upstream.responded',
        exchangeId,
        at: Date.now(),
        status: scenario.status,
        tokenizedResponseBody: compiled.tokenizedResponseBody,
        upstreamMs,
      })
    })

    after(jitter(7600, 400), () => {
      events.emit({
        type: 'rehydration.completed',
        exchangeId,
        at: Date.now(),
        responseBody: compiled.responseBody,
        restored: compiled.entities.length,
      })
    })

    after(jitter(9000, 400), () => {
      const totalMs = scannedMs + upstreamMs + Math.round(jitter(14, 22))
      events.emit({ type: 'exchange.delivered', exchangeId, at: Date.now(), totalMs })
      events.emit({
        type: 'request.completed',
        requestId,
        at: Date.now(),
        status: scenario.status,
        responseBytes: compiled.responseBody.length,
        durationMs: totalMs,
      })
    })
  }

  // Assets arrive in bursts, the way a page load actually looks.
  const assetLoop = () => {
    if (!running) return
    const burst = 1 + Math.floor(Math.random() * 3)
    for (let index = 0; index < burst; index += 1) {
      after(index * jitter(90, 220), () => {
        const clean = Math.random() < 0.22
        runPlain(clean ? pick(CLEAN) : pick(PASSTHROUGH), clean ? 'clean' : 'passthrough')
      })
    }
    after(jitter(900, 1800), assetLoop)
  }

  const treatedLoop = () => {
    if (!running) return
    runTreated()
    after(jitter(6400, 2800), treatedLoop)
  }

  return {
    start() {
      if (running) return
      running = true
      last = { state: 'connecting', endpoint: ENDPOINT }
      status.emit(last)

      after(420, () => {
        events.emit({
          type: 'hello',
          version: 2,
          proxy: {
            name: 'sitm-edge-01',
            region: 'Bern',
            mode: 'transparent http/https',
            policy: 'strict-ch',
          },
        })
        last = {
          state: 'live',
          endpoint: ENDPOINT,
          detail: 'replaying canned protocol events',
        }
        status.emit(last)
        assetLoop()
        after(900, treatedLoop)
      })
    },
    stop() {
      running = false
      for (const id of timers) window.clearTimeout(id)
      timers.clear()
      last = { state: 'closed', endpoint: ENDPOINT }
      status.emit(last)
    },
    onEvent: events.subscribe,
    onStatus(handler) {
      handler(last)
      return status.subscribe(handler)
    },
  }
}

function summarize(kinds: string[]): string {
  const counts = new Map<string, number>()
  for (const kind of kinds) counts.set(kind, (counts.get(kind) ?? 0) + 1)
  return [...counts].map(([kind, n]) => (n > 1 ? `${n}× ${kind}` : kind)).join(', ')
}
