/**
 * The web UI and the API behind it.
 *
 * One static page, no build step, no dependencies -- the same rule the sender's UI is
 * written to, and for the same reason: a harness that needs a toolchain of its own is a
 * harness people stop running.
 *
 * Everything a run produces is already a file under data/runs. These endpoints mostly
 * hand those files out, which is why a run stays readable after this process is gone.
 */
import http from 'node:http'
import { readFile } from 'node:fs/promises'
import { fileURLToPath } from 'node:url'
import { dirname, join } from 'node:path'

import { settings, publicSettings, updateSettings } from './config.js'
import { renderDocumentReport } from './report.js'
import { listRuns, readSummary, readReport, readDocument, isRunId, isDocumentId } from './runs.js'

const PUBLIC_DIR = join(dirname(fileURLToPath(import.meta.url)), '..', 'public')

function json(res, status, value) {
  const body = JSON.stringify(value)
  res.writeHead(status, {
    'content-type': 'application/json; charset=utf-8',
    'content-length': Buffer.byteLength(body),
    'cache-control': 'no-store',
  })
  res.end(body)
}

/** A markdown download. The file name is what the browser saves it as. */
function markdown(res, filename, text) {
  const body = Buffer.from(text, 'utf8')
  res.writeHead(200, {
    'content-type': 'text/markdown; charset=utf-8',
    'content-length': body.length,
    'content-disposition': `attachment; filename="${filename}"`,
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
 * @param deps  { documents, groundTruth, gazetteer, policy, runner, ca }
 */
export function startUi(deps) {
  const { documents, groundTruth, gazetteer, policy, runner, ca } = deps

  const corpus = documents.map((document) => ({
    id: document.id,
    title: document.title,
    category: document.category,
    path: document.path,
    bytes: document.bytes,
    lines: document.lines,
    entities: (groundTruth.byDocument.get(document.id) ?? []).length,
    redactable: (groundTruth.byDocument.get(document.id) ?? []).filter((entity) => entity.policy === 'redact').length,
  }))

  const server = http.createServer(async (req, res) => {
    const url = new URL(req.url, 'http://localhost')
    const segments = url.pathname.split('/').filter(Boolean)

    try {
      if (req.method === 'GET' && (url.pathname === '/' || url.pathname === '/index.html')) {
        const page = await readFile(join(PUBLIC_DIR, 'index.html'))
        res.writeHead(200, { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store' })
        return res.end(page)
      }

      // Everything the page needs to draw its header, its controls and its run list.
      if (req.method === 'GET' && url.pathname === '/api/state') {
        return json(res, 200, {
          config: publicSettings(),
          progress: runner.state(),
          runs: await listRuns(settings.dataDir, settings.maxRunsListed),
          corpus: { documents: corpus.length, bytes: corpus.reduce((sum, item) => sum + item.bytes, 0) },
          groundTruth: {
            people: gazetteer.people.length,
            organisations: gazetteer.organisations.length,
            totals: groundTruth.totals,
            policy: { redact: [...policy.redact].sort(), informational: [...policy.informational].sort(), keep: policy.keep },
          },
          ca: ca ? { subject: ca.subject, fingerprint: ca.fingerprint, validTo: ca.validTo } : null,
        })
      }

      if (req.method === 'GET' && url.pathname === '/api/corpus') {
        return json(res, 200, corpus)
      }

      // The derived ground truth for one document, so the page can show what the harness
      // believes is in it before any run has happened.
      if (req.method === 'GET' && url.pathname === '/api/groundtruth') {
        const id = url.searchParams.get('doc') ?? ''
        const document = documents.find((candidate) => candidate.id === id)
        if (!document) return json(res, 404, { error: `no document ${id}` })
        return json(res, 200, { document: { ...document, text: undefined }, text: document.text, entities: groundTruth.byDocument.get(id) ?? [] })
      }

      if (url.pathname === '/api/config') {
        if (req.method === 'GET') return json(res, 200, publicSettings())
        if (req.method === 'POST') return json(res, 200, updateSettings(await readJsonBody(req)))
        return json(res, 405, { error: 'GET or POST' })
      }

      if (req.method === 'POST' && url.pathname === '/api/run') {
        const patch = await readJsonBody(req)
        const times = Math.min(50, Math.max(1, Math.trunc(Number(patch.times)) || 1))
        const label = typeof patch.label === 'string' ? patch.label.slice(0, 200) : null
        return json(res, 202, { ...runner.start(settings.dataDir, times, label), progress: runner.state() })
      }

      if (req.method === 'POST' && url.pathname === '/api/cancel') {
        return json(res, 200, { ...runner.cancel(), progress: runner.state() })
      }

      if (req.method === 'GET' && url.pathname === '/api/runs') {
        return json(res, 200, await listRuns(settings.dataDir, settings.maxRunsListed))
      }

      // /api/runs/:runId[/report.md | /documents/:docId[/report.md]]
      if (req.method === 'GET' && segments[0] === 'api' && segments[1] === 'runs' && segments[2]) {
        const runId = segments[2]
        if (!isRunId(runId)) return json(res, 400, { error: 'not a run id' })

        if (segments.length === 3) return json(res, 200, await readSummary(settings.dataDir, runId))

        if (segments.length === 4 && segments[3] === 'report.md') {
          return markdown(res, `replacement-quality-${runId}.md`, await readReport(settings.dataDir, runId))
        }

        if (segments[3] === 'documents' && segments[4]) {
          const documentId = decodeURIComponent(segments[4])
          if (!isDocumentId(documentId)) return json(res, 400, { error: 'not a document id' })
          const detail = await readDocument(settings.dataDir, runId, documentId)

          if (segments[5] === 'report.md') {
            const summary = await readSummary(settings.dataDir, runId)
            return markdown(res, `${documentId}-${runId}.md`, renderDocumentReport(summary, detail))
          }
          return json(res, 200, detail)
        }
      }

      return json(res, 404, { error: 'no such endpoint', path: url.pathname })
    } catch (cause) {
      // A missing run or document reaches here as an ENOENT from readFile. It is a 404,
      // not a 500: asking for a run that was never written is a normal thing for a page
      // with a bookmark in it to do.
      const status = cause.code === 'ENOENT' ? 404 : 500
      return json(res, status, { error: cause.message })
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
