/**
 * Writes the derived ground truth out as one JSON file per document, so it can be read,
 * corrected by hand and put under version control.
 *
 * The derivation is good but it is not an oracle: it will miss a name the corpus never
 * vouches for, and it will occasionally claim one it should not. Anything written here is
 * loaded back in preference to the derivation on the next run (see loadOverrides), which
 * is what makes a correction stick rather than being argued with every time.
 *
 *   docker compose -f evaluation/docker-compose.yml run --rm \
 *     -v "$PWD/evaluation/groundtruth:/groundtruth" evaluator node src/tools/dumpGroundTruth.js
 *
 * Or, from a checkout with Node 22 and nothing else:
 *
 *   cd integration/evaluation/evaluator
 *   CORPUS_DIR=../testdata GROUNDTRUTH_DIR=../groundtruth node src/tools/dumpGroundTruth.js
 */
import { mkdir, writeFile } from 'node:fs/promises'
import { join } from 'node:path'

import { settings } from '../config.js'
import { loadCorpus } from '../corpus.js'
import { buildGazetteer } from '../gazetteer.js'
import { buildGroundTruth, resolvePolicy } from '../groundtruth.js'

const documents = await loadCorpus(settings.corpusDir)
const gazetteer = buildGazetteer(documents)
const policy = resolvePolicy()
const groundTruth = buildGroundTruth(documents, gazetteer, policy)

await mkdir(settings.groundTruthDir, { recursive: true })

// The gazetteer first: it is the part worth reading as a whole, because a name missing
// from it is a name missing from every document at once.
await writeFile(
  join(settings.groundTruthDir, 'gazetteer.json'),
  JSON.stringify({ derivedAt: new Date().toISOString(), ...gazetteer }, null, 2),
  'utf8',
)

for (const document of documents) {
  const entities = groundTruth.byDocument.get(document.id) ?? []
  await writeFile(
    join(settings.groundTruthDir, `${document.id}.json`),
    JSON.stringify(
      {
        _comment:
          'Derived from the corpus. Edit freely: this file is loaded in preference to the derivation. ' +
          'Delete it to go back to whatever the derivation says. `start` and `end` are character offsets ' +
          'into the document, end exclusive, and must match `text` exactly or the entity will never be found.',
        document: { id: document.id, path: document.path, title: document.title, bytes: document.bytes },
        entities: entities.map(({ kind, text, start, end, tier, policy: rule, line }) => ({
          kind,
          text,
          start,
          end,
          tier,
          policy: rule,
          line,
        })),
      },
      null,
      2,
    ),
    'utf8',
  )
}

console.log(
  JSON.stringify({
    at: new Date().toISOString(),
    event: 'ground-truth-written',
    directory: settings.groundTruthDir,
    documents: documents.length,
    people: gazetteer.people.length,
    organisations: gazetteer.organisations.length,
    entities: groundTruth.totals.redact + groundTruth.totals.keep + groundTruth.totals.informational,
  }),
)
