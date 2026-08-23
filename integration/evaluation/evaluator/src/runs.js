/**
 * Runs on disk.
 *
 * A run is a directory named for the moment it started, and it is self-contained: the
 * summary the UI reads, the report someone downloads, and the three copies of every
 * document so a finding can be re-read months later without the proxy, the corpus or
 * this process still being around.
 *
 *   data/runs/2026-08-23T09-14-02-118Z/
 *     summary.json                  everything the UI needs to list and open the run
 *     groundtruth.json              what the run was scored against
 *     report.md                     the download
 *     documents/DOC-003.json        findings, substitutions, restoration failures
 *     documents/DOC-003.sent.md     what left the evaluator
 *     documents/DOC-003.received.md what the destination host saw
 *     documents/DOC-003.returned.md what came back to the client
 *
 * Nothing is ever deleted or overwritten. Repeating a run is what this harness is for,
 * and the runs are only worth keeping if comparing two of them is possible.
 */
import { mkdir, readdir, readFile, writeFile } from 'node:fs/promises'
import { join } from 'node:path'

/** `2026-08-23T09:14:02.118Z` -> `2026-08-23T09-14-02-118Z`, which is a legal path everywhere. */
export function runIdFor(date = new Date()) {
  return date.toISOString().replace(/[:.]/g, '-')
}

/** A run id is used as a path segment, so it may only be what this function produces. */
export function isRunId(value) {
  return typeof value === 'string' && /^\d{4}-\d{2}-\d{2}T\d{2}-\d{2}-\d{2}-\d{3}Z$/.test(value)
}

/** A document id reaches disk and a URL, so it is held to the same rule. */
export function isDocumentId(value) {
  return typeof value === 'string' && /^[\w.-]{1,120}$/.test(value) && !value.includes('..')
}

const runsRoot = (dataDir) => join(dataDir, 'runs')

export async function ensureDataDir(dataDir) {
  await mkdir(runsRoot(dataDir), { recursive: true })
}

export function runDir(dataDir, runId) {
  return join(runsRoot(dataDir), runId)
}

export async function beginRun(dataDir, runId) {
  const directory = runDir(dataDir, runId)
  await mkdir(join(directory, 'documents'), { recursive: true })
  return directory
}

/** The per-document artifacts. Written as the run goes, so a crash still leaves evidence. */
export async function writeDocument(dataDir, runId, documentId, { sent, received, returned, result }) {
  const directory = join(runDir(dataDir, runId), 'documents')
  await writeFile(join(directory, `${documentId}.json`), JSON.stringify(result, null, 2), 'utf8')
  await writeFile(join(directory, `${documentId}.sent.md`), sent ?? '', 'utf8')
  await writeFile(join(directory, `${documentId}.received.md`), received ?? '', 'utf8')
  await writeFile(join(directory, `${documentId}.returned.md`), returned ?? '', 'utf8')
}

/**
 * The ground truth the run was scored against, stored with the run.
 *
 * Without it a report is unfalsifiable six months later: the corpus will have moved on,
 * the derivation with it, and nothing on disk would say what "967 protected values" meant
 * on the day. With it, an old run can be re-read and argued with.
 */
export async function writeGroundTruth(dataDir, runId, groundTruth) {
  await writeFile(join(runDir(dataDir, runId), 'groundtruth.json'), JSON.stringify(groundTruth, null, 2), 'utf8')
}

export async function writeSummary(dataDir, runId, summary) {
  await writeFile(join(runDir(dataDir, runId), 'summary.json'), JSON.stringify(summary, null, 2), 'utf8')
}

export async function writeReport(dataDir, runId, markdown) {
  await writeFile(join(runDir(dataDir, runId), 'report.md'), markdown, 'utf8')
}

export async function readSummary(dataDir, runId) {
  return JSON.parse(await readFile(join(runDir(dataDir, runId), 'summary.json'), 'utf8'))
}

export async function readReport(dataDir, runId) {
  return readFile(join(runDir(dataDir, runId), 'report.md'), 'utf8')
}

export async function readDocument(dataDir, runId, documentId) {
  const directory = join(runDir(dataDir, runId), 'documents')
  const [result, sent, received, returned] = await Promise.all([
    readFile(join(directory, `${documentId}.json`), 'utf8').then(JSON.parse),
    readFile(join(directory, `${documentId}.sent.md`), 'utf8'),
    readFile(join(directory, `${documentId}.received.md`), 'utf8').catch(() => ''),
    readFile(join(directory, `${documentId}.returned.md`), 'utf8').catch(() => ''),
  ])
  return { ...result, sent, received, returned }
}

/**
 * Every run on disk, newest first. A directory whose summary cannot be read is listed as
 * incomplete rather than skipped -- an interrupted run is a fact about the proxy, and
 * hiding it would be the one kind of dishonesty a harness cannot afford.
 */
export async function listRuns(dataDir, limit) {
  let entries
  try {
    entries = await readdir(runsRoot(dataDir), { withFileTypes: true })
  } catch {
    return []
  }

  const ids = entries
    .filter((entry) => entry.isDirectory() && isRunId(entry.name))
    .map((entry) => entry.name)
    .sort()
    .reverse()
    .slice(0, limit)

  const runs = []
  for (const id of ids) {
    try {
      const summary = await readSummary(dataDir, id)
      runs.push({ id, complete: true, ...summary.headline, at: summary.startedAt, label: summary.label ?? null })
    } catch {
      runs.push({ id, complete: false, at: null, label: null })
    }
  }
  return runs
}
