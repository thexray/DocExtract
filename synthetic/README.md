# Synthetic receipts

Every receipt here is fictitious and self-made — businesses, people, addresses, and
registration numbers are invented; any resemblance is coincidental. They exist so the
failure-taxonomy section of the main README can show real-looking receipt images without
redistributing anything from SROIE or CORD.

Each file targets one failure pattern observed in the eval mismatches:

| File | Pattern it reproduces |
|---|---|
| `01-person-name-trap.html` | Person's name printed above the store name — models extract it as `company` |
| `02-ambiguous-date.html` | All-numeric day-first date (`03/04/26`) — month/day-order confusion |
| `03-multi-currency.html` | Two currencies on one receipt — wrong `total`/currency picked |
| `04-non-english.html` | Non-English receipt (Latvian, EUR, PVN) — field labels not recognized |
| `05-sum-mismatch.html` | Line items deliberately don't sum to the printed total — validator flag, not a model error |
| `06-handwriting.html` | Handwriting-style amounts — OCR digit misreads |

Pipeline for the README shots: open in a browser → print at 80 mm width → photograph the
paper (the photo step supplies the real-world noise: skew, shadows, low resolution) →
run `docextract extract` on the photo.
