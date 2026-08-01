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

 the accepted artifacts are also a corpus:

 lexical retrieval ── BM25 in-repo, $0/query, one receipt per document
        │             └─ optional query rewrite / rerank, kept only if measured
        ▼
 answer + groundedness ── cite doc IDs; citations must be in context and the
                          figure must match ground truth — all checked in code
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

## Asking the corpus questions

Extraction produces a corpus, and the next thing anyone wants is to ask it something. This
slice adds retrieval and question answering over the artifacts already on disk — no
re-extraction, no vector database, no embedding provider. Receipts are short, so one receipt
is one document and the baseline is a BM25 index that lives in this repository. It costs $0
per query, which is the point: an improvement has to beat free before it earns its API calls.

The questions are derived mechanically from the SROIE ground truth — never hand-written, never
model-written. Single-receipt lookups use (vendor, date) pairs that identify exactly one
receipt; aggregates cover every receipt the ground truth attributes to one vendor. A question
is only emitted when its relevant-document set is provably complete, because a relevant set
that is merely probable silently caps recall and looks like a retriever problem.

The index is built from what the extractor produced, not from the answer key. A receipt whose
vendor was misread is genuinely hard to find by vendor name, and indexing the ground truth
instead would hide that behind numbers that no longer describe the system anyone would run.

<!-- retrieval-results:begin -->
| Run | Pipeline | Docs | Questions | Recall@1 | Recall@5 | Recall@10 | MRR | Grounded | Cost |
|---|---|---|---|---|---|---|---|---|---|
| bm25-baseline | bm25 | 250 | 30 | 72.1% | 94.7% | 98.0% | 0.922 | n/a | $0.0000 |
| bm25 | bm25 | 250 | 30 | 72.1% | 94.7% | 98.0% | 0.922 | 28/30 | $0.7507 |
| bm25+rerank | bm25+rerank | 250 | 30 | 78.4% | 98.0% | 98.0% | 1.000 | 29/30 | $1.6889 |
<!-- retrieval-results:end -->

Read that with the scale in mind: 30 questions over 250 documents is a demo, not a benchmark.

The first row is the same retrieval as the second, scored without generating any answers. Its
cost column is the honest version of "retrieval is free": identical recall for $0.0000, which
locates every cent of the other two rows in answer generation rather than in search.

**The reranker was kept because the numbers asked for it, not because it sounded good.** It
moves recall@1 from 72.1% to 78.4%, recall@5 from 94.7% to 98.0%, and MRR from 0.922 to 1.000
— a relevant receipt is now ranked first for every question in the set, and single-receipt
lookups reach 100% at rank 1. It also converts one ungrounded answer into a grounded one, by
pulling a receipt into the top-5 context that the lexical pass had left at rank 6. Had these
columns not moved, the honest write-up would have been that a 98%-recall@10 baseline did not
need fixing.

What it costs is the other half of that sentence: reranking more than doubles the bill, from
$0.75 to $1.69, because it adds a model call per question to reorder documents the free pass
had already found. On this corpus that buys ranking quality, not reach.

Two things the table does not say on its own. Recall@1 is bounded above for aggregate
questions — one with three relevant receipts cannot beat 0.33 at k=1 — so the k=5 and k=10
columns are the ones to compare across pipelines. And reranking only permutes candidates the
lexical pass already found, so it can move recall@1, recall@5 and MRR, but recall@10 is fixed
by the baseline no matter how good the reranker is. That is visible in the table rather than
merely asserted: recall@10 is 98.0% in both pipelines, to the decimal.

### Groundedness is a check, not an opinion

An answer is scored by three deterministic predicates, not by asking a model whether it did
well:

1. it cites at least one document ID;
2. every cited ID is a document that was actually in its context — a citation it was never
   shown is fabricated provenance, which is worse than no citation at all;
3. the figure it reports equals the ground-truth figure.

An LLM judge would have been fewer lines and worth nothing here, because it would share the
failure modes of the thing under test.

That third predicate is not redundant, and one recorded failure shows why. On an aggregate
question the top-ranked receipt was relevant, but only two of the five receipts that belonged
in the answer made it into the top-5 context. The model then did exactly what it was asked:
it summed what it was shown, and reported $39.70 against a true $102.10. Every citation it
gave was a document it had actually been shown, so **a citation-only groundedness check would
have passed this answer**. Only comparing the figure to ground truth caught it.

The second recorded failure is the encouraging one. With nothing relevant in its context at
all, the model returned no figure rather than inventing a plausible one.

Both failures are aggregate questions, and both are retrieval failures that surfaced
downstream — the answering step was faithful to its context in each case. That is the argument
for measuring retrieval separately instead of judging the final answer alone: an
answer-only metric would have blamed the wrong component.

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
#   eval --retrieval [--questions N] [--k N] [--rewrite] [--rerank] [--no-answers]
#                            recall@k / MRR / groundedness over the artifacts already
#                            extracted; --no-answers is the $0 retrieval-only pass
#   report                   results tables (regenerates the blocks above) + cost vs budget
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
- Retrieval is scored over 30 questions and 250 documents. That is enough to rank two
  pipelines against each other and not enough to publish as a benchmark; treat the deltas as
  directional and the absolute numbers as a demo.
- Aggregate questions are capped at five relevant receipts per vendor. Larger fan-outs would
  measure how skewed the corpus is more than how well retrieval works.
- Cost figures come from the headless CLI, which charges a fixed per-invocation overhead —
  measured at ~$0.018 before a single prompt token — on top of tokens. A retrieval answer here
  costs ~$0.025, of which roughly three quarters is that floor rather than the prompt. Read
  the cost column as the price of this delivery mechanism, not as raw API token pricing.
- The published results table predates the retry path, so those numbers include no retried
  documents. Retry recovers malformed *responses*, not misread *fields* — the accuracy columns
  would move only for documents lost to unparseable output, and the table was not re-run to
  claim that, because re-extracting a scored corpus to chase a rounding difference is exactly
  the kind of spend the budget exists to refuse.

## Colophon

Built with Claude Code as the delivery engine; the architecture, validation rules, eval
design, and every review decision are mine. .NET 8, C#, headless Claude CLI, JSONL storage.
