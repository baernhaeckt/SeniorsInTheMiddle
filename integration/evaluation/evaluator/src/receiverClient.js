/**
 * What the destination host actually received, read back from it directly.
 *
 * This is the only call the evaluator makes that does not go through the proxy, and it
 * has to be: the whole measurement is the difference between what the client sent and
 * what the far end saw, and the proxy is the thing standing between them. Asking the
 * proxy would be asking the defendant.
 */
import http from 'node:http'

function request(method, url, timeoutMs) {
  return new Promise((resolve, reject) => {
    const call = http.request(url, { method, timeout: timeoutMs }, (response) => {
      const chunks = []
      response.on('data', (chunk) => chunks.push(chunk))
      response.on('end', () => {
        const body = Buffer.concat(chunks).toString('utf8')
        if (response.statusCode >= 400) {
          return reject(new Error(`receiver answered ${response.statusCode}: ${body.slice(0, 200)}`))
        }
        try {
          resolve(JSON.parse(body))
        } catch (cause) {
          reject(new Error(`receiver sent something that is not JSON: ${cause.message}`))
        }
      })
    })
    call.on('timeout', () => call.destroy(new Error(`timed out after ${timeoutMs} ms`)))
    call.on('error', reject)
    call.end()
  })
}

export function createReceiverClient({ baseUrl, timeoutMs = 5000 }) {
  const at = (path) => new URL(path, baseUrl).toString()

  return {
    /** The exact bytes of one request body as they arrived, or null if nothing was captured. */
    async capture(runId, documentId) {
      const result = await request(
        'GET',
        at(`/_harness/eval/capture?run=${encodeURIComponent(runId)}&doc=${encodeURIComponent(documentId)}`),
        timeoutMs,
      )
      return result.found ? result : null
    },

    /** Frees a run's captures. Bodies are held in memory, and a corpus pass is megabytes. */
    async release(runId) {
      return request('DELETE', at(`/_harness/eval/captures?run=${encodeURIComponent(runId)}`), timeoutMs)
    },

    async ready() {
      return request('GET', at('/_harness/eval/health'), timeoutMs)
    },
  }
}
