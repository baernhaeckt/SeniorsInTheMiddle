/**
 * Request bodies exactly as they arrived, kept for the evaluation harness to read back.
 *
 * The evaluator needs the one view nobody else has: the body as the destination host saw
 * it, after the proxy was done with it and before anything here touches it. It cannot get
 * that from its own response, because by the time the answer reaches the client the proxy
 * has put the real values back -- which is the feature working, and is exactly what hides
 * the thing being measured.
 *
 * So a request carrying an `X-Eval-Run` and `X-Eval-Doc` pair is filed here verbatim, and
 * the evaluator collects it over plain HTTP straight to this process. Ordinary traffic is
 * unaffected: no header, no capture, no cost.
 *
 * This is memory, and a corpus pass is megabytes of it, so a run is expected to release
 * its captures when it is done. The caps below are what happens when one does not.
 */

const MAX_BODIES = Number(process.env.EVAL_MAX_BODIES ?? 2000)
const MAX_BYTES = Number(process.env.EVAL_MAX_BYTES ?? 128 * 1024 * 1024)

/** Header values are ASCII and are used as map keys, so they are held to a shape. */
const ID = /^[\w.:-]{1,120}$/

export function createEvalStore() {
  /** run id -> (document id -> capture). A Map, so insertion order is eviction order. */
  const runs = new Map()
  let bodies = 0
  let bytes = 0

  function evictOldest() {
    for (const [runId, documents] of runs) {
      for (const [documentId, capture] of documents) {
        documents.delete(documentId)
        bodies -= 1
        bytes -= capture.bytes
        if (documents.size === 0) runs.delete(runId)
        return
      }
    }
  }

  return {
    /** Files one body. Returns false when the headers do not name a run and a document. */
    capture(runId, documentId, body, meta) {
      if (!ID.test(runId ?? '') || !ID.test(documentId ?? '')) return false

      const size = Buffer.byteLength(body)
      let documents = runs.get(runId)
      if (!documents) runs.set(runId, (documents = new Map()))

      const existing = documents.get(documentId)
      if (existing) {
        bodies -= 1
        bytes -= existing.bytes
      }

      documents.set(documentId, { body, bytes: size, at: new Date().toISOString(), ...meta })
      bodies += 1
      bytes += size

      while ((bodies > MAX_BODIES || bytes > MAX_BYTES) && bodies > 1) evictOldest()
      return true
    },

    get(runId, documentId) {
      return runs.get(runId)?.get(documentId) ?? null
    },

    release(runId) {
      const documents = runs.get(runId)
      if (!documents) return 0
      for (const capture of documents.values()) {
        bodies -= 1
        bytes -= capture.bytes
      }
      runs.delete(runId)
      return documents.size
    },

    health() {
      return {
        ok: true,
        runs: runs.size,
        bodies,
        bytes,
        limits: { maxBodies: MAX_BODIES, maxBytes: MAX_BYTES },
      }
    },
  }
}
