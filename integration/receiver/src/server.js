/**
 * The receiver: an ordinary destination host, reachable over HTTP and HTTPS.
 *
 * It knows nothing about the proxy. That is the point -- it answers whatever arrives and
 * reports what it saw, so the harness can compare the two ends of the exchange.
 *
 * Its HTTPS certificate is signed by the harness CA (see ../../pki/gen-certs.sh), which
 * the proxy container is pointed at through SSL_CERT_FILE. Without that the proxy's
 * upstream handshake in ConnectProxyMiddleware would fail, because it validates
 * certificates normally.
 */
import http from 'node:http'
import https from 'node:https'
import { readFileSync } from 'node:fs'

import { handle, readsBody } from './routes.js'
import { inspect } from './pii.js'
import { createStats, record, snapshot } from './stats.js'

const HTTP_PORT = Number(process.env.HTTP_PORT ?? 3000)
const HTTPS_PORT = Number(process.env.HTTPS_PORT ?? 3443)
const TLS_CERT = process.env.TLS_CERT ?? '/pki/receiver.crt'
const TLS_KEY = process.env.TLS_KEY ?? '/pki/receiver.key'
const MAX_BODY = Number(process.env.MAX_BODY_BYTES ?? 2_000_000)

const stats = createStats()
let seq = 0

/**
 * The names the sender says it put in the body. Base64 of UTF-8, because header values
 * are ASCII and half the fixture names carry an umlaut.
 */
function declaredNames(header) {
  if (!header) return []
  try {
    return Buffer.from(header, 'base64')
      .toString('utf8')
      .split('|')
      .map((name) => name.trim())
      .filter(Boolean)
  } catch {
    return []
  }
}

async function readBody(req) {
  const chunks = []
  let size = 0
  for await (const chunk of req) {
    size += chunk.length
    if (size > MAX_BODY) throw new Error(`body above ${MAX_BODY} bytes`)
    chunks.push(chunk)
  }
  return Buffer.concat(chunks)
}

function listener(scheme) {
  return async (req, res) => {
    const startedAt = process.hrtime.bigint()
    const url = new URL(req.url, `${scheme}://${req.headers.host ?? 'receiver'}`)
    const ctx = {
      seq: ++seq,
      scheme,
      method: req.method,
      pathname: url.pathname,
      query: url.searchParams,
      headers: req.headers,
      body: '',
      inspection: null,
    }

    let outcome
    try {
      const raw = await readBody(req)
      ctx.requestBytes = raw.length

      // Only bodies a real destination would parse get looked at. Static assets are
      // served without reading them, which is exactly how the proxy classifies them.
      if (readsBody(ctx.pathname)) {
        ctx.body = raw.toString('utf8')
        ctx.inspection = inspect(ctx.body, declaredNames(req.headers['x-harness-names']))
      }

      outcome = await handle(ctx, res, { stats: () => snapshot(stats) })
    } catch (cause) {
      if (!res.headersSent) {
        const body = JSON.stringify({ error: cause.message })
        res.writeHead(400, { 'content-type': 'application/json', 'content-length': Buffer.byteLength(body) })
        res.end(body)
      } else {
        res.end()
      }
      outcome = { status: 400, responseBytes: 0 }
    }

    const durationMs = Number(process.hrtime.bigint() - startedAt) / 1e6
    const entry = {
      scheme,
      route: ctx.pathname,
      method: ctx.method,
      status: outcome.status,
      requestBytes: ctx.requestBytes ?? 0,
      responseBytes: outcome.responseBytes ?? 0,
      inspection: ctx.inspection,
    }
    record(stats, entry)

    // One JSON line per request. `sawRawPii` is the line to watch: once the proxy
    // redacts, a destination host should never see a real identifier again.
    console.log(
      JSON.stringify({
        at: new Date().toISOString(),
        seq: ctx.seq,
        ...entry,
        durationMs: Number(durationMs.toFixed(1)),
        sawRawPii: ctx.inspection?.sawRawPii ?? null,
        tokens: ctx.inspection?.tokenCount ?? null,
      }),
    )
  }
}

http.createServer(listener('http')).listen(HTTP_PORT, () => {
  console.log(JSON.stringify({ at: new Date().toISOString(), event: 'listening', scheme: 'http', port: HTTP_PORT }))
})

// TLS is optional so the harness still starts if certgen has not run: an HTTP-only
// receiver is a degraded harness, a crash-looping one is no harness at all.
try {
  const options = { cert: readFileSync(TLS_CERT), key: readFileSync(TLS_KEY) }
  https.createServer(options, listener('https')).listen(HTTPS_PORT, () => {
    console.log(
      JSON.stringify({ at: new Date().toISOString(), event: 'listening', scheme: 'https', port: HTTPS_PORT }),
    )
  })
} catch (cause) {
  console.error(
    JSON.stringify({
      at: new Date().toISOString(),
      event: 'https-disabled',
      reason: cause.message,
      hint: `Expected ${TLS_CERT} and ${TLS_KEY}. Did the certgen service run?`,
    }),
  )
}
