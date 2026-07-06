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

## Colophon

Built with Claude Code as the delivery engine; the architecture, validation rules, eval
design, and every review decision are mine. .NET 8, C#, headless Claude CLI, JSONL storage.
