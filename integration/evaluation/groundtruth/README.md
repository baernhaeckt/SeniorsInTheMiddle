# Hand-corrected ground truth

Empty by default: the harness derives what is in each document from the corpus itself at
start, and that is usually right.

When it is not — a name the corpus never vouches for, an address the pattern claims too
much of — write the correction here and it sticks. A `<DOC-ID>.json` in this directory is
used **instead of** the derivation for that document. Delete the file to go back.

Export the current derivation to start from:

```bash
cd integration
docker compose -f evaluation/docker-compose.yml run --rm evaluator \
  node src/tools/dumpGroundTruth.js
```

That writes one file per document plus `gazetteer.json`, which is the part worth reading
whole: a name missing there is a name missing from every document at once.

`start` and `end` are character offsets into the document, end exclusive, and must match
`text` exactly. An entity whose offsets no longer line up is dropped and reported at
start — a corpus edited after its sidecar was written would otherwise be scored against
text that is not there any more, and silently.
