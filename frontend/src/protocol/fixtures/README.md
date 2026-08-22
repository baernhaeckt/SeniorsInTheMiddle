# Protocol fixtures

Recorded frames, one JSON array per file, in the order the proxy sends them.
`../types.test.ts` asserts that every frame in every file parses and that the
whole sequence reduces without error. The backend can use the same files as
expected-output snapshots for its emitter.
