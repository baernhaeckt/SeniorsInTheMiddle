# Replacement quality — run 2026-08-23T08-21-59-390Z

_final_

**376 of 967 protected values reached the destination host unchanged** (61.1% covered). Every value the client sent came back to the client. 16 values on the keep list were changed anyway — a difference from the policy in force, not necessarily a fault.

## Headline

| Measure | Value | What it means |
| --- | ---: | --- |
| Leaked | 376 | Protected values the destination host saw unchanged. Must be 0. |
| Coverage | 61.1% | Share of protected values that were replaced. |
| Coverage, unambiguous only | 61.5% | Full names and shaped identifiers. |
| Coverage, bare names | 55.9% | A surname or first name on its own. Harder, reported apart. |
| Restoration | 100.0% | Share of protected values the client got back. |
| Not returned | 0 | Values the client sent and did not get back. Must be 0. |
| Over-redaction | 16 | Values on the keep list that were changed anyway. A policy difference, not necessarily a fault. |
| Precision | 97.1% | Of the substitutions the policy has a view on, how many landed on a protected value. |
| Precision, strict floor | 84.8% | Every substitution the ground truth cannot vouch for counted as wrong. The true figure is between the two, and the gap is the list under “worth a look”. |
| Documents fully clean | 2 / 49 | Nothing protected reached the destination. |
| Documents fully intact | 49 / 49 | Everything came back to the client. |
| Exchanges failed | 0 | Never completed a request through the proxy. |

## By kind of value

| Kind | Expected | Replaced | Partly | Leaked | Not returned | Coverage |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| PERSON | 387 | 265 | 9 | 113 | 0 | 70.8% |
| PHONE | 268 | 9 | 0 | 259 | 0 | 3.4% |
| EMAIL | 226 | 226 | 0 | 0 | 0 | 100.0% |
| ADDRESS | 55 | 47 | 8 | 0 | 0 | 100.0% |
| IBAN | 23 | 23 | 0 | 0 | 0 | 100.0% |
| AHV | 4 | 0 | 0 | 4 | 0 | 0.0% |
| BIRTHDATE | 4 | 4 | 0 | 0 | 0 | 100.0% |

## By document category

| Category | Documents | Protected values | Leaked | Not returned |
| --- | ---: | ---: | ---: | ---: |
| chat | 4 | 52 | 4 | 0 |
| email | 6 | 114 | 25 | 0 |
| meeting | 5 | 58 | 5 | 0 |
| notification | 4 | 44 | 4 | 0 |
| ticket | 20 | 191 | 35 | 0 |
| wiki | 10 | 508 | 303 | 0 |

## Substitutions worth a look

Neither group below is scored, and the two are not the same accusation.

**over-redaction** is measured against the policy in force: the value was on the keep list in `policy.json` and was changed anyway. That list is a choice, not a law — a proxy that redacts every mail address including the published invoicing one is defensible, and moving the value out of `keep` is the right answer if that is the intent.

**unclassified** is not an accusation at all. The proxy changed something the ground truth has no entry for, which is as often the proxy being right about something the derivation missed — an abbreviated first name, a hostname — as it is the proxy reaching too far into ordinary prose. Reading a few is the only way to tell them apart, which is why they are listed rather than counted.

| Document | Verdict | Original | Became |
| --- | --- | --- | --- |
| DOC-005 | over-redaction | rechnung@natron.io | ppeukert@example.org |
| DOC-006 | over-redaction | rechnung@natron.io | ppeukert@example.org |
| DOC-019 | over-redaction | rechnung@natron.io | ppeukert@example.org |
| DOC-020 | over-redaction | rechnung@natron.io | ppeukert@example.org |
| DOC-021 | over-redaction | rechnung@natron.io | ppeukert@example.org |
| DOC-021 | over-redaction | Güterstrasse 24, 3008 Bern | Maria-Luise-Schweitzer-Gasse 76-99, 20504 Wiedenbrück |
| DOC-031 | over-redaction | rechnung@natron.io | Junkenring 761, 51593 Herzberg: ppeukert@example.org |
| DOC-032 | over-redaction | rechnung@natron.io | Junkenring 761, 51593 Herzberg: ppeukert@example.org |
| DOC-033 | over-redaction | rechnung@natron.io | Junkenring 761, 51593 Herzberg: ppeukert@example.org |
| DOC-034 | over-redaction | rechnung@natron.io | Junkenring 761, 51593 Herzberg: ppeukert@example.org |
| DOC-035 | over-redaction | rechnung@natron.io | ppeukert@example.org |
| DOC-036 | over-redaction | rechnung@natron.io | Junkenring 761, 51593 Herzberg: ppeukert@example.org |
| DOC-041 | over-redaction | rechnung@natron.io | ppeukert@example.org |
| DOC-042 | over-redaction | rechnung@natron.io | ppeukert@example.org |
| DOC-043 | over-redaction | rechnung@natron.io | ppeukert@example.org |
| DOC-044 | over-redaction | rechnung@natron.io | ppeukert@example.org |
| DOC-001 | unclassified | Fileshares | Alwina Höfig |
| DOC-002 | unclassified | Fileshares | Alwina Höfig |
| DOC-003 | unclassified | Badge-Zugang | Hartungweg 8, 77976 Zerbst |
| DOC-003 | unclassified | Alarmversand | Metin Bender |
| DOC-004 | unclassified | Badge-Zugang | Hartungweg 8, 77976 Zerbst |
| DOC-004 | unclassified | Alarmversand | Metin Bender |
| DOC-007 | unclassified | Arbeitsplatz | Vinzenz Salz |
| DOC-009 | unclassified |  | Kunde: Rosita-Röhricht-Gasse 1562, 54031 Stuttgart Treuhand Genossenschaft (K-69900) |
| DOC-009 | unclassified | Für | Anfrage von Dragica Nette, Bernard Boucsein B.Eng.Für |
| DOC-011 | unclassified | Backup-Job | Loni Austermühle |
| DOC-012 | unclassified | Backup-Job | Loni Austermühle |
| DOC-028 | unclassified | Eskalationsmatrix | Luigi Tintzmann |
| DOC-028 | unclassified | Personaldossier | Reni-Hofmann-Allee 987, 93109 Biedenkopf |
| DOC-042 | unclassified | simo | Ernstring 37-39, 86652 Flöha |
| DOC-043 | unclassified | seli | Nadine Paffrath |

## Documents

| Document | Category | Protected | Leaked | Not returned | Over-redacted | Status | ms |
| --- | --- | ---: | ---: | ---: | ---: | --- | ---: |
| DOC-041 — #betrieb, Störung Jolimont Metallbau GmbH | chat | 13 | 1 | 0 | 1 | see detail | 616.9 |
| DOC-042 — #betrieb, Störung Bremgarte Medizintechnik GmbH | chat | 13 | 1 | 0 | 1 | see detail | 618.5 |
| DOC-043 — #betrieb, Störung Selhofen Holzbau AG | chat | 13 | 1 | 0 | 1 | see detail | 68.3 |
| DOC-044 — #betrieb, Störung Jolimont Immobilien GmbH | chat | 13 | 1 | 0 | 1 | see detail | 63.9 |
| DOC-031 — Vertragsverlängerung Selhofen Metallbau AG (NAT-2024-2572) | email | 19 | 4 | 0 | 1 | see detail | 77.9 |
| DOC-032 — Vertragsverlängerung Chasseral Bauchemie AG (NAT-2026-9240) | email | 19 | 4 | 0 | 1 | see detail | 74.1 |
| DOC-033 — Vertragsverlängerung Zytglogge Metallbau AG (NAT-2023-3348) | email | 19 | 4 | 0 | 1 | see detail | 83.2 |
| DOC-034 — Vertragsverlängerung Elfenau Reinigung Genossenschaft (NAT-2025-3958) | email | 19 | 4 | 0 | 1 | see detail | 80 |
| DOC-035 — Vertragsverlängerung Selhofen Holzbau AG (NAT-2025-6300) | email | 19 | 4 | 0 | 1 | see detail | 81.2 |
| DOC-036 — Vertragsverlängerung Weissbühl Reinigung AG (NAT-2025-3726) | email | 19 | 5 | 0 | 1 | see detail | 76.8 |
| DOC-045 — Quartalsgespräch Chasseral Verpackungen GmbH | meeting | 12 | 1 | 0 | 0 | see detail | 59.5 |
| DOC-046 — Quartalsgespräch Selhofen Holzbau AG | meeting | 12 | 1 | 0 | 0 | see detail | 55.5 |
| DOC-047 — Quartalsgespräch Dählhölzli Reinigung AG | meeting | 11 | 1 | 0 | 0 | see detail | 55.8 |
| DOC-048 — Quartalsgespräch Ostring Treuhand AG | meeting | 11 | 1 | 0 | 0 | see detail | 52.1 |
| DOC-049 — Quartalsgespräch Bremgarte Reinigung AG | meeting | 12 | 1 | 0 | 0 | see detail | 57.4 |
| DOC-037 — [Ticketsystem] K-44607: Kommentar von Reto Hofer | notification | 11 | 1 | 0 | 0 | see detail | 55.3 |
| DOC-038 — [Ticketsystem] K-81026: Kommentar von Sandra Bieri | notification | 11 | 1 | 0 | 0 | see detail | 47 |
| DOC-039 — [Ticketsystem] K-59946: Kommentar von Fabienne Stucki | notification | 11 | 1 | 0 | 0 | see detail | 43.3 |
| DOC-040 — [Ticketsystem] K-72440: Kommentar von Livia Marti | notification | 11 | 1 | 0 | 0 | see detail | 51.3 |
| DOC-001 — VPN-Zugang funktioniert nicht mehr für Regula Zbinden | ticket | 15 | 4 | 0 | 0 | see detail | 46.2 |
| DOC-002 — VPN-Zugang funktioniert nicht mehr für Nadine Huber | ticket | 15 | 5 | 0 | 0 | see detail | 60.3 |
| DOC-003 — Offboarding Miriam Schmid per Monatsende | ticket | 15 | 2 | 0 | 0 | see detail | 56.8 |
| DOC-004 — Offboarding Katrin Kunz per Monatsende | ticket | 15 | 1 | 0 | 0 | see detail | 60.3 |
| DOC-005 — Rückfrage zur Rechnung NAT-2024-5898 | ticket | 7 | 1 | 0 | 1 | see detail | 56.8 |
| DOC-006 — Rückfrage zur Rechnung NAT-2025-6300 | ticket | 7 | 1 | 0 | 1 | see detail | 50.2 |
| DOC-007 — Neuer Mitarbeiter Monika Keller ab 2026-09-25 | ticket | 13 | 4 | 0 | 0 | see detail | 48 |
| DOC-008 — Neuer Mitarbeiter Corinne Wyss ab 2026-09-18 | ticket | 13 | 2 | 0 | 0 | see detail | 57.1 |
| DOC-009 — Firewall-Regel für Standort Wohlen bei Bern | ticket | 7 | 0 | 0 | 0 | clean | 58.4 |
| DOC-010 — Firewall-Regel für Standort Murten | ticket | 7 | 1 | 0 | 0 | see detail | 48.3 |
| DOC-011 — Backup-Job schlägt fehl bei Belpberg Holzbau AG | ticket | 7 | 1 | 0 | 0 | see detail | 43.6 |
| DOC-012 — Backup-Job schlägt fehl bei Chasseral Textil GmbH | ticket | 7 | 2 | 0 | 0 | see detail | 43.8 |
| DOC-013 — Rufnummernportierung Riedbach Verpackungen GmbH | ticket | 9 | 2 | 0 | 0 | see detail | 40.2 |
| DOC-014 — Rufnummernportierung Bärental Energie GmbH | ticket | 9 | 2 | 0 | 0 | see detail | 42.7 |
| DOC-015 — Zertifikatserneuerung portal.frienisberg-metallbau.ch | ticket | 6 | 1 | 0 | 0 | see detail | 38.9 |
| DOC-016 — Zertifikatserneuerung portal.baerental-baumanagement.ch | ticket | 6 | 0 | 0 | 0 | clean | 41 |
| DOC-017 — Storage-Erweiterung Elfenau Elektro AG um 2 TB | ticket | 7 | 1 | 0 | 0 | see detail | 37.4 |
| DOC-018 — Storage-Erweiterung Weissbühl Landtechnik AG um 2 TB | ticket | 7 | 1 | 0 | 0 | see detail | 48.9 |
| DOC-019 — Mail delivery delayed for Belpberg Vermessung AG | ticket | 9 | 2 | 0 | 1 | see detail | 44.1 |
| DOC-020 — Mail delivery delayed for Chasseral Textil AG | ticket | 10 | 2 | 0 | 1 | see detail | 531.2 |
| DOC-021 — Kontaktliste Kundenbetreuung | wiki | 376 | 262 | 0 | 2 | see detail | 549.2 |
| DOC-022 — Onboarding Chasseral Verpackungen GmbH | wiki | 16 | 6 | 0 | 0 | see detail | 57.6 |
| DOC-023 — Onboarding Zytglogge Metallbau AG | wiki | 13 | 5 | 0 | 0 | see detail | 40.7 |
| DOC-024 — Onboarding Ostring Getränke Genossenschaft | wiki | 16 | 5 | 0 | 0 | see detail | 59 |
| DOC-025 — Onboarding Belpberg Holzbau AG | wiki | 16 | 5 | 0 | 0 | see detail | 53.9 |
| DOC-026 — Onboarding Selhofen Landtechnik AG | wiki | 13 | 4 | 0 | 0 | see detail | 53.7 |
| DOC-027 — Onboarding Bantiger Logistik GmbH | wiki | 14 | 4 | 0 | 0 | see detail | 50.7 |
| DOC-028 — Eskalationsmatrix Pikettdienst | wiki | 24 | 7 | 0 | 0 | see detail | 58.8 |
| DOC-029 — Runbook: nightly restore verification (Ostring Treuhand AG) | wiki | 10 | 2 | 0 | 0 | see detail | 51.6 |
| DOC-030 — Runbook: nightly restore verification (Frienisberg Metallbau GmbH) | wiki | 10 | 3 | 0 | 0 | see detail | 32.1 |

## What was measured, and how

Every document was sent through the proxy as the user message of a chat completion. Three copies were then compared: the markdown as it left the evaluator, the same body as it arrived at the destination host (read back from the receiver directly, not through the proxy), and what came back to the client after the proxy restored the real values.

The proxy substitutes plausible stand-ins rather than `[PERSON_1]`-style tokens, so a redacted body looks exactly like an unredacted one and searching it for personal-data shapes proves nothing. What the figures above rest on instead is the alignment between the copies: which spans of the document the proxy changed. A protected value inside a changed span was hidden; one inside an unchanged span leaked.

**Partly** in the tables above means an identifying fragment of the value survived. A fragment counts as identifying when it contains a word of four letters or more — `Bern`, `Beat`, `Freiburgstrasse`, the parts a reader recognises — or when it covers more than 40% of the original, which catches half an AHV number even though it has no letters in it.

Everything else is a remnant, not a partial hit. `Sulgenrainweg 80, 3250 Lyss` comes back with the street replaced and the town replaced and `80, 3250` still standing — a house number and a postcode, no word in either. Both identifying parts are gone, so that is a value the proxy hid. What survived is recorded on every finding and shown in the detail view regardless, so the judgement can be checked rather than taken on trust.

Ground truth was derived from the corpus itself — 101 people and 60 organisations, each vouched for by structure in the documents (a contact-list row, an angle-bracketed address, a chat prefix) or by a mail local part matching the name. Nothing is hand-listed, so the corpus can change without a dictionary changing with it.

Policy applied to the ground truth:

- **Must be hidden**: ADDRESS, AHV, BIRTHDATE, EMAIL, IBAN, PERSON, PHONE
- **Must survive unchanged**: `rechnung@natron.io`, `no-reply@ticketsystem.example`, `+41 31 528 00 00`, `Güterstrasse 24, 3008 Bern`
- **Counted, never scored**: CONTRACT_ID, DATE, MONEY, ORG, TICKET_ID — whether these should be hidden is a policy question, so what happened to them is reported and left for the reader.

## Run

| Field | Value |
| --- | --- |
| Run | 2026-08-23T08-21-59-390Z |
| Started | 2026-08-23T08:21:59.431Z |
| Finished | 2026-08-23T08:22:02.933Z |
| Proxy | proxy:3128 |
| Destination | receiver.sitm.local |
| Scheme | https (CONNECT, intercepted) |
| Connection to proxy | plain (:3128) |
| Workers | 2 |
| Proxy CA | CN=SeniorsInTheMiddle Proxy CA |
| Proxy CA fingerprint | A1:9A:1C:15:F1:F0:98:E5:3F:43:14:89:1A:B4:BA:D6:87:7D:BF:12:4D:08:A3:7A:38:1E:FF:60:5D:58:39:2D |
| Substitutions made | 677 |
| … on a protected value | 574 |
| … on a value meant to survive | 17 |
| … on a company name, town, date, contract or amount | 71 |
| … on something the ground truth does not cover | 15 |


_Only the first two lines are scored. The rest are cases this harness has declined to judge — the corpus’s own company names, towns and contract numbers, and substitutions the ground truth has no entry for at all. Both are listed under “substitutions worth a look”, because calling them either way would be a guess._
