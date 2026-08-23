/**
 * The corpus: every .md file under testdata, read once at start.
 *
 * A document is identified by its DOC-nnn prefix where it has one and by its path
 * otherwise, because that id ends up in a header, in a file name on disk and in a URL.
 * It has to survive all three without escaping.
 */
import { readdir, readFile } from 'node:fs/promises'
import { join, relative, sep } from 'node:path'

/** Walks a directory tree and returns every .md file, sorted so a run is reproducible. */
async function markdownFiles(root) {
  const found = []

  async function walk(directory) {
    const entries = await readdir(directory, { withFileTypes: true })
    for (const entry of entries.sort((a, b) => a.name.localeCompare(b.name, 'en'))) {
      const path = join(directory, entry.name)
      if (entry.isDirectory()) await walk(path)
      else if (entry.isFile() && entry.name.toLowerCase().endsWith('.md')) found.push(path)
    }
  }

  await walk(root)
  return found
}

/**
 * `testdata/ticket/DOC-003-offboarding-....md` -> id `DOC-003`, category `ticket`.
 *
 * A file without a DOC prefix still gets a stable id from its path, so the corpus does
 * not have to be named a particular way to be usable.
 */
function identify(root, path) {
  const rel = relative(root, path).split(sep).join('/')
  const category = rel.includes('/') ? rel.slice(0, rel.indexOf('/')) : 'root'
  const name = rel.slice(rel.lastIndexOf('/') + 1).replace(/\.md$/i, '')
  const numbered = name.match(/^(DOC-\d+)/i)
  return {
    id: numbered ? numbered[1].toUpperCase() : name.replace(/[^\w.-]+/g, '-'),
    category,
    name,
    path: rel,
  }
}

export async function loadCorpus(root) {
  const files = await markdownFiles(root)
  const documents = []
  const seen = new Set()

  for (const file of files) {
    const meta = identify(root, file)
    // Two files with the same DOC number would overwrite each other's artifacts on disk.
    let id = meta.id
    for (let suffix = 2; seen.has(id); suffix++) id = `${meta.id}-${suffix}`
    seen.add(id)

    const text = await readFile(file, 'utf8')
    documents.push({
      ...meta,
      id,
      // The title line is what the UI lists, and it is nearly always the first heading.
      title: (text.match(/^#\s+(.+)$/m)?.[1] ?? meta.name).trim(),
      bytes: Buffer.byteLength(text),
      lines: text.split('\n').length,
      text,
    })
  }

  return documents
}
