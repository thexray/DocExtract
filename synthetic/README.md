# Synthetic receipts

Every receipt here is fictitious and self-made — businesses, people, addresses, and
registration numbers are invented; any resemblance is coincidental. They exist so the
failure-taxonomy section of the main README can show real-looking receipt images without
redistributing anything from SROIE or CORD.

Each receipt targets one failure pattern observed in the eval mismatches. The `.html` file
is the source; the `.jpg` beside it is the printed sheet photographed, and is what the
main README shows.

| Receipt | Pattern it reproduces |
|---|---|
| `01-person-name-trap` | Person's name printed above the store name — models extract it as `company` |
| `02-ambiguous-date` | All-numeric day-first date (`03/04/26`) — month/day-order confusion |
| `03-multi-currency` | Two currencies on one receipt — wrong `total`/currency picked |
| `04-non-english` | Non-English receipt (Latvian, EUR, PVN) — field labels not recognized |
| `05-sum-mismatch` | Line items deliberately don't sum to the printed total — validator flag, not a model error |
| `06-handwriting` | Handwriting-style amounts — OCR digit misreads |

Pipeline for the README shots: open in a browser → print (see below) → photograph the
paper → run `docextract extract` on the photo.

The photo step is what makes these images real input rather than rendered HTML: the capture
carries paper grain, ink bleed into the fibre, and the softening of small type that no
screenshot reproduces. It is not a stress test of camera conditions — the shots are
straight-on and evenly lit, so extraction failures on them are attributable to the receipt's
own trap rather than to skew or shadow. Photographs are JPEG (q90, ~400 KB each); PNG stores
paper grain losslessly and cost 6× more for no visible gain.

## Printing all six at once

`sheet.html` lays out all six on a single A4 page (2 columns × 3 rows) for one print run.
Cut along the light dashed guides — they form a straight grid, so it is one vertical cut
and three horizontal ones, not six separate rectangles. Receipts sit 3 mm inside the
guides, so scissors never touch the printed area; where a receipt is shorter than its row,
the offcut carries a blank tail, which is harmless for the photo.

Print at **100 % scale with headers/footers off** — the layout measures 194 × 267 mm inside
an 8 mm margin, so it has ~14 mm of vertical slack. "Fit to page" or "shrink to fit" will
resize it and waste that clearance.

The receipt markup in `sheet.html` is a **copy** of the six individual files, with the type
scaled to 12 px so three rows fit the page. The individual files stay canonical: edit a
receipt there first, then mirror the change into `sheet.html`. Nothing checks that the two
agree.
