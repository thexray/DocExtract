# DocExtract

An LLM document-extraction pipeline that publishes its own report card. Receipts and
invoices go in; validated, typed JSON comes out — and every accuracy number in this README
is regenerated from stored eval runs by `docextract report`, never hand-edited.

Most extraction demos show a cherry-picked success. This project inverts that: a cheap model
does the reading, a deterministic validator decides what can be trusted, an eval harness
measures field-level accuracy against public ground truth, and the failures get counted and
categorized instead of cropped out.

## How it works

```
 receipt image / PDF
        │
        ▼
 vision extraction ──── Claude Haiku, headless CLI, Read tool only;
        │               per-field self-reported confidence
        │               └─ output unparseable? one retry, billed and logged
        ▼
 deterministic validation ── dates parse, totals positive, currency whitelist,
        │                    line items sum to total, confidence floor —
        │                    the LLM never grades its own output
        ├── all rules pass ──────────► extractions/accepted/
        └── any violation ──────────► extractions/needs-review/  (violations listed)
        │
        ▼
 eval harness ── field accuracy + exact match vs. SROIE ground truth;
        │        failed docs feed a targeted escalation pass (stronger model,
        │        only where measured accuracy says it pays)
        ▼
 report ── results table below + month-to-date cost vs. a hard budget
```

Every LLM call is cost-logged to an append-only ledger. Extraction stops scheduling new
calls when month-to-date spend reaches the budget — the cap is enforced by code, not by
intention.

### When a call comes back wrong

Models sometimes answer a "return JSON" instruction with prose, or with JSON wrapped in
apology. That is a transport-shaped failure, not a data-shaped one, so it is retried once
with the required shape restated — and the retry is visible from three sides: the artifact
records `attempts: 2` and the exact parse error it recovered from, the ledger gets a second
line under `extract:retry`, and the document's cost is the sum of both calls. `report` prints
the month's retry rate and what retries cost.

Two rules keep this from becoming a way to launder bad output. The retry limit is bounded
(`ClaudeCli:MaxAttempts`, default 2, hard-clamped at 3) because an unbounded loop on a paid
call is a runaway bill, not resilience. And a retry only decides whether a document was
*read* — never whether the reading was *right*. That verdict stays with the deterministic
validator, which the retry never re-runs, relaxes, or gets a second opinion from. A document
that fails both attempts lands in `needs-review` with both attempts charged for.

`scripts/stub-claude-cli.cmd` is a CLI test double that fails on demand, so the recovery path
can be exercised without waiting for a real model to misbehave.

## Results

Field-level accuracy against the [SROIE](https://rrc.cvc.uab.es/?ch=13) (ICDAR 2019) ground
truth; comparison is normalization-tolerant (case, whitespace, punctuation, date formats),
so the numbers measure reading, not formatting luck.

<!-- eval-results:begin -->
| Run | Models | Docs | Company | Date | Address | Total | Exact match | Cost | $/doc | Avg s/doc |
|---|---|---|---|---|---|---|---|---|---|---|
| haiku-250 | claude-haiku-4-5 | 250 | 74.4% | 76.4% | 66.3% | 92.0% | 42.4% | $8.99 | $0.036 | 18.8 |
| haiku+escalation-250 | claude-haiku-4-5+claude-sonnet-5 | 250 | 86.0% | 98.8% | 85.5% | 98.4% | 72.8% | $20.14 | $0.081 | 15.6 |
<!-- eval-results:end -->

Line-item extraction exists in the schema but is not scored here: SROIE's ground truth has
no line items. Scoring it against [CORD](https://github.com/clovaai/cord) (CC BY 4.0,
Indonesian receipts) is the natural extension.

## Datasets and licensing

No dataset files are committed to this repository. SROIE's original license terms are
unclear (community mirrors relicense only annotations), and while CORD is CC BY 4.0, the
posture is uniform: `scripts/download-datasets.ps1` fetches everything locally. Sample
imagery in this README, when present, is self-made synthetic receipts.

## Running it

```powershell
dotnet build DocExtract.slnx
dotnet run --project DocExtract/DocExtract.csproj -- <verb>

# verbs:
#   extract <file|dir|list.txt> [--parallel N] [--tier extraction|escalation] [--limit N]
#   eval [--label NAME]      score extraction artifacts against ground truth
#   report                   results table (regenerates the block above) + cost vs budget
#   check                    config smoke check (reports key presence only)
```

Configuration layers: `appsettings.json` (committed, non-secret defaults) →
`appsettings.Development.json` (gitignored) → environment variables.

## Honest limitations

- Company names and addresses are scored with normalization-tolerant exact match; a model
  reading "SDN BHD" where the receipt prints "SDN. BHD." ties, but a paraphrase does not —
  near-misses count as misses.
- The validator's needs-review flags are conservative by design: a receipt with a service
  charge can fail the line-items-sum rule while every extracted field is correct.
- Confidence values are the model's self-assessment. They gate the review split; they are
  deliberately not used by the eval, which trusts only ground truth.
- Scanned-receipt quality varies wildly; the failure examples recorded per eval run are the
  actual error surface, not a curated subset.
- The published results table predates the retry path, so those numbers include no retried
  documents. Retry recovers malformed *responses*, not misread *fields* — the accuracy columns
  would move only for documents lost to unparseable output, and the table was not re-run to
  claim that, because re-extracting a scored corpus to chase a rounding difference is exactly
  the kind of spend the budget exists to refuse.

## Colophon

Built with Claude Code as the delivery engine; the architecture, validation rules, eval
design, and every review decision are mine. .NET 8, C#, headless Claude CLI, JSONL storage.
