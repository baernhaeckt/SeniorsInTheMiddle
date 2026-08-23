/**
 * The destination host, as far as the proxy is concerned.
 *
 * Each route exists to put one shape of traffic through the proxy: a body worth reading,
 * a body that must never be read, a response that arrives in chunks, a slow upstream, an
 * error, something too big for a single 8 KiB StreamProxy chunk, something that is not
 * valid UTF-8.
 *
 * A route handler writes the response itself and returns what it wrote.
 */
import { setTimeout as delay } from 'node:timers/promises'

const CSS = `:root{--ink:#111}\nbody{font:16px/1.5 system-ui;color:var(--ink)}\n.card{padding:1rem}\n`
const JS = `export const version='1.0.0';\nexport function boot(){console.log('hello from the receiver')}\n`
const SVG = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32"><circle cx="16" cy="16" r="14" fill="#2b6cb0"/></svg>`

/** A JPEG header followed by noise: deliberately not valid UTF-8. */
function jpeg(bytes = 6144) {
  const buffer = Buffer.alloc(bytes)
  buffer.writeUInt16BE(0xffd8, 0) // SOI
  for (let i = 2; i < bytes - 2; i++) buffer[i] = (i * 37 + 11) % 256
  buffer.writeUInt16BE(0xffd9, bytes - 2) // EOI
  return buffer
}

const ASSETS = {
  '/assets/app.css': ['text/css; charset=utf-8', Buffer.from(CSS)],
  '/assets/app.js': ['application/javascript; charset=utf-8', Buffer.from(JS)],
  '/assets/logo.svg': ['image/svg+xml', Buffer.from(SVG)],
  '/assets/photo.jpg': ['image/jpeg', jpeg()],
}

function send(res, status, contentType, payload, headers = {}) {
  const body = Buffer.isBuffer(payload) ? payload : Buffer.from(payload)
  res.writeHead(status, {
    'content-type': contentType,
    'content-length': body.length,
    'x-harness-receiver': '1',
    ...headers,
  })
  res.end(body)
  return { status, responseBytes: body.length }
}

function json(res, status, value, headers) {
  return send(res, status, 'application/json; charset=utf-8', JSON.stringify(value, null, 2), headers)
}

/**
 * Answers a request. `ctx.body` is the raw request body; `ctx.inspection` is what the
 * receiver made of it, and is null for routes whose body is never read.
 */
export async function handle(ctx, res, deps) {
  const { pathname } = ctx
  // HEAD is a GET whose body Node drops on the way out, so routes match it as one.
  // `curl -I` through the proxy is the obvious first thing anyone tries by hand.
  const method = ctx.method === 'HEAD' ? 'GET' : ctx.method

  if (pathname === '/health') {
    return send(res, 204, 'text/plain', '')
  }

  if (pathname === '/_harness/stats') {
    return json(res, 200, deps.stats())
  }

  // The evaluation harness reads these directly, never through the proxy. See evalStore.js.
  if (pathname === '/_harness/eval/health' && method === 'GET') {
    return json(res, 200, deps.evalStore.health())
  }

  if (pathname === '/_harness/eval/capture' && method === 'GET') {
    const capture = deps.evalStore.get(ctx.query.get('run') ?? '', ctx.query.get('doc') ?? '')
    return json(res, 200, capture ? { found: true, ...capture } : { found: false })
  }

  if (pathname === '/_harness/eval/captures' && ctx.method === 'DELETE') {
    return json(res, 200, { released: deps.evalStore.release(ctx.query.get('run') ?? '') })
  }

  const asset = ASSETS[pathname]
  if (asset) {
    if (method !== 'GET') return json(res, 405, { error: 'GET only' })
    const [contentType, payload] = asset
    return send(res, 200, contentType, payload, { 'cache-control': 'public, max-age=31536000' })
  }

  if (pathname === '/api/v1/status' && method === 'GET') {
    return json(res, 200, {
      service: 'harness-receiver',
      scheme: ctx.scheme,
      now: new Date().toISOString(),
      ok: true,
    })
  }

  // The PII carrier. Echoes the fields straight back, so the sender can tell whether a
  // value survived the round trip through redaction and rehydration.
  if (pathname === '/api/v1/forms/intake' && method === 'POST') {
    const parsed = parseJson(ctx.body)
    if (!parsed.ok) return json(res, 400, { error: 'invalid json', detail: parsed.error })
    return json(res, 201, {
      received: parsed.value,
      storedAs: `case-${ctx.seq}`,
      note: 'The receiver stored exactly what reached it.',
    })
  }

  // An LLM-shaped destination: the request text comes back inside the answer, which is
  // what makes rehydration observable from the client side.
  if (pathname === '/api/v1/chat/completions' && method === 'POST') {
    const parsed = parseJson(ctx.body)
    if (!parsed.ok) return json(res, 400, { error: 'invalid json', detail: parsed.error })
    const prompt = parsed.value?.messages?.at(-1)?.content ?? ''
    return json(res, 200, {
      id: `chatcmpl-harness-${ctx.seq}`,
      model: parsed.value?.model ?? 'harness-echo-1',
      // The prompt exactly as it arrived, for an evaluation request only. The answer
      // below quotes it too, but with a sentence in front, and the evaluator compares
      // this against what it sent character for character -- a prefix it has to strip is
      // a prefix it can strip wrongly.
      ...(ctx.headers['x-eval-doc'] ? { echo: prompt } : {}),
      choices: [
        {
          index: 0,
          finish_reason: 'stop',
          message: {
            role: 'assistant',
            content: `Verstanden. Ich fasse zusammen, was mich erreicht hat: ${prompt}`,
          },
        },
      ],
      usage: { prompt_tokens: prompt.length, completion_tokens: prompt.length },
    })
  }

  if (pathname === '/upload' && method === 'POST') {
    return json(res, 202, { accepted: true, bytes: Buffer.byteLength(ctx.body) })
  }

  // Several writes with a pause between them, so the tunnel carries more than one chunk.
  if (pathname === '/stream/chunks' && method === 'GET') {
    const count = clamp(Number(ctx.query.get('count') ?? 5), 1, 50)
    res.writeHead(200, {
      'content-type': 'application/x-ndjson; charset=utf-8',
      'transfer-encoding': 'chunked',
      'x-harness-receiver': '1',
    })
    let bytes = 0
    for (let i = 1; i <= count; i++) {
      const line = `${JSON.stringify({ chunk: i, of: count, at: Date.now() })}\n`
      res.write(line)
      bytes += Buffer.byteLength(line)
      await delay(40)
    }
    res.end()
    return { status: 200, responseBytes: bytes }
  }

  if (pathname === '/slow' && method === 'GET') {
    const ms = clamp(Number(ctx.query.get('ms') ?? 750), 0, 20000)
    await delay(ms)
    return json(res, 200, { sleptMs: ms })
  }

  const error = pathname.match(/^\/error\/(\d{3})$/)
  if (error) {
    const status = clamp(Number(error[1]), 400, 599)
    return json(res, status, { error: `deliberate ${status}` })
  }

  return json(res, 404, { error: 'no such route', path: pathname })
}

function parseJson(body) {
  try {
    return { ok: true, value: body ? JSON.parse(body) : null }
  } catch (cause) {
    return { ok: false, error: cause.message }
  }
}

function clamp(value, min, max) {
  if (!Number.isFinite(value)) return min
  return Math.min(max, Math.max(min, Math.trunc(value)))
}

/** Routes whose body the receiver reads. Everything else is served without looking. */
export function readsBody(pathname) {
  return pathname === '/api/v1/forms/intake' ||
    pathname === '/api/v1/chat/completions' ||
    pathname === '/upload'
}
