# Evaluation harness

Runs every document in `testdata/` through the proxy and scores what happened to the
personal data in them. Standalone: its own PKI, its own proxy container, its own network,
so it cannot disturb the traffic harness in `../docker-compose.yml` and vice versa.

```bash
cd integration
docker compose -f evaluation/docker-compose.yml up --build
```

Then open **http://localhost:3200** and press **Run**.

## What it answers

The harness next door asks whether the proxy stays up and stays honest under traffic.
This one asks a different question, over a fixed corpus, and gives three numbers:

| | |
| --- | --- |
| **Leaked** | Values that had to be hidden and reached the destination host unchanged. Must be 0. |
| **Not returned** | Values the client sent and did not get back. Must be 0 — a privacy feature that loses data is a data-loss bug with a privacy story attached. |
| **Over-redaction** | Values on the `keep` list — the published hotline, the invoicing address, the no-reply sender — that were changed anyway. A difference from the policy in force, not necessarily a fault. |

Around those: coverage per kind of value, precision, which documents are clean, and every
substitution the proxy made with a verdict on each.

## What runs

| Service | What it is |
| --- | --- |
| `certgen` | One-shot. Issues the harness CA and the receiver's HTTPS certificate into the `pki` volume. |
| `proxy` | **The thing under test.** Built from `../../backend/Dockerfile`, unmodified. |
| `receiver` | Built from `../receiver` — the same image the traffic harness uses. Holds each request body exactly as it arrived. |
| `evaluator` | Sends the corpus through the proxy, scores it, writes it to disk, serves the UI on 3200. |
| `dashboard` | Optional (`--profile dashboard`). The SPA from `../../frontend`, on 8082. |

Ports are offset from the traffic harness so both can run at once: proxy on 3228/3227,
its API on 8180, the receiver on 3010/3453, the UI on 3200.

## Why it is measured this way

The proxy does not splice `[PERSON_1]` into a body. It substitutes a plausible stand-in
drawn by a faker, so `Werner Müller` leaves as `Beat Zbinden` and **a redacted body looks
exactly like an unredacted one**. Searching the destination's copy for personal-data
shapes finds a full set of them and tells you nothing.

So the measurement is an alignment, not a search. Three copies of every document are
compared:

```
sent       the markdown as it left the evaluator
received   the same body as it arrived at the destination host
returned   what came back to the client, after the proxy put the real values back
```

`sent` vs `received` is the redaction, and a word-level diff says which spans of the
document the proxy changed. Overlay the expected values on that and every question has an
answer: a value inside a changed span was hidden, one inside an unchanged span leaked, and
and a changed span covering no expected value at all is a substitution nobody can vouch
for — which is listed for a human to read rather than scored either way.

`sent` vs `returned` is the restoration, and should be empty.

**`received` cannot come from the response.** By the time an answer reaches the client the
proxy has put the real values back — the feature working, and exactly what hides the thing
being measured. So the receiver files each body verbatim under the `X-Eval-Run` and
`X-Eval-Doc` headers, and the evaluator collects it over plain HTTP straight to the
receiver, never through the proxy. Asking the proxy would be asking the defendant.

## Ground truth

Derived from the corpus itself, not hand-listed, so `testdata/` can grow or be replaced
without a dictionary changing with it.

Identifiers with a shape — addresses, IBANs, AHV numbers, `+41` numbers, mail addresses,
dates — come from patterns. Names cannot: `Werner Müller` and `Guten Tag` are the same two
capitalised words to a regex, and a regex alone would put half the German language into
the ground truth and make the precision figure a fiction. So a name is only accepted once
some structure in the corpus vouches for it — a contact-list row, an angle-bracketed
address, a chat prefix, a `Von:` line — or its normalised form is the local part of an
address that appears somewhere in the corpus. Nobody writes `guten.tag@` anywhere.

Every value carries a **policy**, in `policy.json`:

- **redact** — must not reach the destination. Missing one is a leak.
- **keep** — must reach the destination unchanged.
- **informational** — counted and shown, never scored. Company names, town names, IPs,
  contract numbers, amounts, ordinary dates. Whether those should be hidden is a policy
  question this harness has no business deciding, so it reports what happened and leaves
  the judgement to whoever reads it. Move `ORG` or `LOCATION` into `redact` to score them.

  Towns are worth a word: a town is derived as whatever follows a four-digit postcode in a
  full address, which then lets `Standort Murten` and `am Standort Lyss` be recognised
  everywhere they appear on their own. Without that they are in no category at all, and a
  proxy that hides them — defensible, a bare town is location data — gets counted as
  having rewritten something nobody asked about.

And a **tier**. `strong` is a full name or a shaped identifier — an unambiguous reference,
and the number the harness leads with. `weak` is a bare surname or first name that another
document ties to a person (`Nyffeler klärt intern`, `Guten Tag Werner`): still personal
data, genuinely harder to find, reported on its own so it neither inflates the headline
figure nor disappears from it.

### Correcting it by hand

The derivation is good but it is not an oracle. `groundtruth/` holds one
`<DOC-ID>.json` per document, and **a sidecar there is used instead of the derivation**
for that document — which is what lets a correction stick rather than being argued with on
every run. Export the current derivation to start from:

```bash
docker compose -f evaluation/docker-compose.yml run --rm evaluator node src/tools/dumpGroundTruth.js
```

That writes the per-document files plus `gazetteer.json`, the part worth reading whole: a
name missing there is a name missing from every document at once.

Sidecars are checked in, so what a run was scored against is reviewable in a diff. The
cost is that an improvement to the derivation will not reach a pinned document until its
sidecar is refreshed or deleted — so the count of pinned documents is printed at start,
shown under **Method** and written into every report. An entity whose offsets no longer
match its document is dropped and reported, rather than silently scoring against text that
is not there any more.

Each run also writes its own `groundtruth.json`, so an old report stays falsifiable after
the corpus has moved on.

### The judgements, written down rather than hidden in a percentage

**Partly replaced.** A value counts as only partly hidden when an *identifying* fragment
survives: a word of four letters or more — `Bern`, `Beat`, `Freiburgstrasse`, the parts a
reader recognises — or a fragment covering more than 40% of the value, which catches half
an AHV number even though it has no letters in it.

Everything else is a remnant, not a partial hit. `Sulgenrainweg 80, 3250 Lyss` comes back
with the street replaced *and* the town replaced and `80, 3250` still standing — a house
number and a postcode, no word in either, a quarter of the string. Both identifying parts
are gone, so that is a value the proxy hid. Getting this wrong is not academic: under a
cruder rule 52 of 55 addresses looked like near-misses when they were nothing of the sort.
What survived is recorded on every finding and shown in the detail view either way, so the
judgement can be checked rather than taken on trust.

**Unscored substitutions.** Two kinds, and they are not the same accusation:

- **over-redaction** — the value was on the `keep` list and was changed anyway. Measured
  against the policy in force, and that policy is a choice: a proxy that redacts every mail
  address including the published invoicing one is defensible, and moving the value out of
  `keep` is the right answer if that is the intent.
- **unclassified** — the proxy changed something the ground truth has no entry for. This is
  not an accusation at all. It is as often the proxy being *right* about something the
  derivation missed — an abbreviated first name like `seli`, a hostname — as it is the
  proxy reaching into ordinary prose.

Neither is scored. Both are listed in full under **Worth a look**, because calling them
either way would be a guess. Precision is reported twice for the same reason: once over
the substitutions the policy has a view on, and once as a floor that counts every
unvouched-for substitution as wrong. The true figure is between them.

## Runs on disk

Every run is a self-contained directory under `data/runs/`, named for the moment it
started. Nothing is ever deleted or overwritten: repeating a run is what this harness is
for, and runs are only worth keeping if two of them can be compared.

```
data/runs/2026-08-23T09-14-02-118Z/
  summary.json                    everything the UI lists and opens
  groundtruth.json                what this run was scored against
  report.md                       the download
  documents/DOC-003.json          findings, substitutions, restoration failures
  documents/DOC-003.sent.md       what left the evaluator
  documents/DOC-003.received.md   what the destination host saw
  documents/DOC-003.returned.md   what came back to the client
```

`report.md` is downloadable from the UI, per run and per document. Because the three
copies are on disk beside it, a finding can be re-read months later without the proxy, the
corpus or the evaluator still being around.

The **repeat** box queues that many passes back to back, each stored separately. Every run
on disk stays in the list on the left and opens with one click.

## The UI

`http://localhost:3200` — one static HTML file with inline script, no build step.

- **Overview** — the headline numbers, the split by document category, and where each
  substitution landed.
- **By kind** — what happened to every `PERSON`, `PHONE`, `IBAN`, … in the corpus.
- **Documents** — one row per document; click one for the detail.
- **Worth a look** — every substitution that did not land on an expected value, split
  into keep-list violations and the genuinely unclassified.
- **Method** — what was measured and how, including the policy in force for that run.

A document's detail shows the three copies side by side with the spans painted: green for
replaced, amber for partly, red for leaked and for anything rewritten that had to survive.
Hover a span for the kind, the tier and what the destination saw in its place.

## As a CI step

```bash
EVAL_ON_START=true EVAL_EXIT_AFTER_RUN=true \
  docker compose -f evaluation/docker-compose.yml up --build --abort-on-container-exit
```

The evaluator sends the corpus once as soon as the proxy answers and exits 0 when nothing
leaked, nothing failed and everything came back — non-zero otherwise. The run is still
written to `data/runs/`, so a red build leaves the evidence behind.

Every knob is in `.env.example`.

## Adding to the corpus

Drop a `.md` file anywhere under `testdata/`. The subdirectory becomes its category and a
leading `DOC-nnn` becomes its id. Nothing else has to be edited: the gazetteer, the ground
truth and the report all rebuild from whatever is there at start.

Give a new person at least one structural mention somewhere in the corpus — a contact-list
row, a `Name <mail@host>`, a chat prefix — or a mail address whose local part matches the
name. Without one the harness will not treat it as a name, and a leak it cannot see is a
leak it cannot report.
