/**
 * The testing UI and the small API behind it.
 *
 * One static page, no build step, no dependencies -- the harness should not need a
 * toolchain of its own to show what the proxy is doing. Everything the page needs comes
 * from the five endpoints below.
 */
import http from 'node:http'
import { readFile } from 'node:fs/promises'
import { fileURLToPath } from 'node:url'
import { dirname, join } from 'node:path'

import { publicSettings, updateSettings, settings } from './config.js'
import { ALL_SCENARIOS, PII_SCENARIOS, findScenario } from './scenarios.js'

const PUBLIC_DIR = join(dirname(fileURLToPath(import.meta.url)), '..', 'public')
const RECEIVER_CACHE_MS = 1000

let receiverCache = { at: 0, value: null, error: null }

/** The receiver's own counters, fetched directly -- deliberately not through the proxy. */
function fetchReceiverStats(url, timeoutMs = 1500) {
  return new Promise((resolve) => {
    const request = http.get(url, { timeout: timeoutMs }, (response) => {
      const chunks = []
      response.on('data', (chunk) => chunks.push(chunk))
      response.on('end', () => {
        try {
          resolve({ value: JSON.parse(Buffer.concat(chunks).toString('utf8')), error: null })
        } catch (cause) {
          resolve({ value: null, error: cause.message })
        }
      })
    })
    request.on('timeout', () => request.destroy(new Error('timed out')))
    request.on('error', (cause) => resolve({ value: null, error: cause.message }))
  })
}

async function receiverStats() {
  if (Date.now() - receiverCache.at < RECEIVER_CACHE_MS) return receiverCache
  const result = await fetchReceiverStats(settings.receiverStatsUrl)
  receiverCache = { at: Date.now(), ...result }
  return receiverCache
}

function json(res, status, value) {
  const body = JSON.stringify(value)
  res.writeHead(status, {
    'content-type': 'application/json; charset=utf-8',
    'content-length': Buffer.byteLength(body),
    'cache-control': 'no-store',
  })
  res.end(body)
}

async function readJsonBody(req, limit = 64_000) {
  const chunks = []
  let size = 0
  for await (const chunk of req) {
    size += chunk.length
    if (size > limit) throw new Error('body too large')
    chunks.push(chunk)
  }
  const raw = Buffer.concat(chunks).toString('utf8')
  return raw ? JSON.parse(raw) : {}
}

/**
 * @param store    the record store
 * @param hooks    { ca: () => object, fire: (scenario, scheme, proxyTls) => Promise<record> }
 */
export function startUi({ store, hooks }) {
  const server = http.createServer(async (req, res) => {
    const url = new URL(req.url, 'http://localhost')

    try {
      if (req.method === 'GET' && (url.pathname === '/' || url.pathname === '/index.html')) {
        const page = await readFile(join(PUBLIC_DIR, 'index.html'))
        res.writeHead(200, { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store' })
        return res.end(page)
      }

      if (req.method === 'GET' && url.pathname === '/api/stats') {
        const receiver = await receiverStats()
        return json(res, 200, {
          sender: store.stats(),
          receiver: receiver.value,
          receiverError: receiver.error,
          config: publicSettings(),
          ca: hooks.ca(),
        })
      }

      if (req.method === 'GET' && url.pathname === '/api/events') {
        const since = Number(url.searchParams.get('since') ?? 0)
        return json(res, 200, store.events(Number.isFinite(since) ? since : 0))
      }

      if (url.pathname === '/api/config') {
        if (req.method === 'GET') return json(res, 200, publicSettings())
        if (req.method === 'POST') return json(res, 200, updateSettings(await readJsonBody(req)))
        return json(res, 405, { error: 'GET or POST' })
      }

      if (req.method === 'GET' && url.pathname === '/api/scenarios') {
        return json(
          res,
          200,
          ALL_SCENARIOS.map((scenario) => ({
            name: scenario.name,
            describe: scenario.describe,
            weight: scenario.weight,
            carriesPii: PII_SCENARIOS.includes(scenario),
          })),
        )
      }

      if (req.method === 'POST' && url.pathname === '/api/fire') {
        const patch = await readJsonBody(req)
        const scenario = findScenario(patch.scenario)
        if (!scenario) return json(res, 400, { error: `no scenario named ${patch.scenario}` })
        const scheme = patch.scheme === 'https' ? 'https' : 'http'
        const proxyTls = patch.proxyTls === true || patch.proxyTls === 'tls'
        return json(res, 200, await hooks.fire(scenario, scheme, proxyTls))
      }

      if (req.method === 'GET' && url.pathname === '/api/ca') {
        return json(res, 200, hooks.ca())
      }

      return json(res, 404, { error: 'no such endpoint', path: url.pathname })
    } catch (cause) {
      return json(res, 500, { error: cause.message })
    }
  })

  server.listen(settings.uiPort, () => {
    console.log(
      JSON.stringify({
        at: new Date().toISOString(),
        event: 'ui-listening',
        url: `http://localhost:${settings.uiPort}`,
      }),
    )
  })

  return server
}
