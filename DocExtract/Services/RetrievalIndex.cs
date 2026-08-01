namespace DocExtract.Services;

using System.Globalization;
using System.Text;
using DocExtract.Models;

/// <summary>
/// In-repo BM25 over the extraction artifacts. No vector database, no embedding provider, no
/// chunking: receipts are a few dozen tokens each, so one artifact is one document and any
/// chunking story here would be theatre. Retrieval costs $0 per query, which is the point of
/// making it the baseline — the improvement pass has to beat free before it earns its calls.
///
/// The corpus is deliberately built from what the extractor produced, not from ground truth.
/// A receipt whose vendor was misread is genuinely hard to find by vendor name, and burying
/// that by indexing the answer key would make the recall numbers a fiction.
/// </summary>
public sealed class RetrievalIndex
{
    private const double K1 = 1.2, B = 0.75;

    private readonly List<IndexedDoc> _docs = [];
    private readonly List<Dictionary<string, int>> _termFreqs = [];
    private readonly Dictionary<string, int> _docFreq = new(StringComparer.Ordinal);
    private readonly List<int> _lengths = [];
    private double _avgLength;

    public int Count => _docs.Count;
    public IEnumerable<string> DocIds => _docs.Select(d => d.DocId);

    public static RetrievalIndex Build(IEnumerable<ExtractionArtifact> artifacts)
    {
        var index = new RetrievalIndex();
        foreach (var artifact in artifacts.OrderBy(a => a.Source, StringComparer.Ordinal))
            index.Add(new IndexedDoc(
                Path.GetFileNameWithoutExtension(artifact.Source),
                DocumentText(artifact),
                Summarize(artifact)));

        index._avgLength = index._lengths.Count == 0 ? 0 : index._lengths.Average();
        return index;
    }

    private void Add(IndexedDoc doc)
    {
        var tokens = Tokenize(doc.Text);
        var tf = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var token in tokens) tf[token] = tf.GetValueOrDefault(token) + 1;
        foreach (var term in tf.Keys) _docFreq[term] = _docFreq.GetValueOrDefault(term) + 1;

        _docs.Add(doc);
        _termFreqs.Add(tf);
        _lengths.Add(tokens.Count);
    }

    /// <summary>Top <paramref name="k"/> documents for a query, best first. Ties break on doc
    /// ID so a rerun ranks identically — an unstable tail would show up as phantom movement
    /// between eval runs.</summary>
    public List<Hit> Search(string query, int k)
    {
        var scores = new double[_docs.Count];
        foreach (var term in Tokenize(query))
        {
            if (!_docFreq.TryGetValue(term, out var df)) continue;
            var idf = Math.Log(1 + (_docs.Count - df + 0.5) / (df + 0.5));
            for (var i = 0; i < _docs.Count; i++)
            {
                if (!_termFreqs[i].TryGetValue(term, out var f)) continue;
                var norm = 1 - B + B * _lengths[i] / Math.Max(1e-9, _avgLength);
                scores[i] += idf * (f * (K1 + 1)) / (f + K1 * norm);
            }
        }

        return Enumerable.Range(0, _docs.Count)
            .Where(i => scores[i] > 0)
            .OrderByDescending(i => scores[i])
            .ThenBy(i => _docs[i].DocId, StringComparer.Ordinal)
            .Take(k)
            .Select(i => new Hit(_docs[i].DocId, Math.Round(scores[i], 4)))
            .ToList();
    }

    public IndexedDoc? Get(string docId) =>
        _docs.FirstOrDefault(d => string.Equals(d.DocId, docId, StringComparison.Ordinal));

    /// <summary>
    /// Lowercase alphanumeric runs, singles dropped. Dates split into their components on
    /// both sides of the comparison ("2018-06-08" and "08/06/2018" both yield 2018/06/08),
    /// which is the same normalization tolerance the extraction eval grants — the retriever
    /// is being measured on finding the right receipt, not on date formatting luck.
    /// </summary>
    internal static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) current.Append(ch);
            else { Flush(); }
        }
        Flush();
        return tokens;

        void Flush()
        {
            if (current.Length > 1) tokens.Add(current.ToString());
            current.Clear();
        }
    }

    /// <summary>The searchable surface of one artifact: every extracted value, no confidences.
    /// Dates are emitted ISO so they tokenize into the same components a question does.</summary>
    private static string DocumentText(ExtractionArtifact artifact)
    {
        var d = artifact.Extraction;
        var sb = new StringBuilder();
        sb.AppendLine(Path.GetFileNameWithoutExtension(artifact.Source));
        sb.AppendLine(d?.Vendor?.Value ?? "");
        sb.AppendLine(d?.Address?.Value ?? "");
        sb.AppendLine(IsoDate(d?.Date?.Value));
        sb.AppendLine(d?.Currency?.Value ?? "");
        sb.AppendLine(d?.Total?.Value?.ToString("0.00", CultureInfo.InvariantCulture) ?? "");
        foreach (var item in d?.LineItems ?? []) sb.AppendLine(item.Description ?? "");
        return sb.ToString();
    }

    /// <summary>One line per document for the answer prompt — enough to answer from, short
    /// enough that top-k context stays a few hundred tokens rather than the whole corpus.</summary>
    private static string Summarize(ExtractionArtifact artifact)
    {
        var d = artifact.Extraction;
        var id = Path.GetFileNameWithoutExtension(artifact.Source);
        var total = d?.Total?.Value?.ToString("0.00", CultureInfo.InvariantCulture) ?? "?";
        var items = d?.LineItems is { Count: > 0 } li
            ? " | items: " + string.Join("; ", li.Take(6).Select(i => i.Description).Where(s => !string.IsNullOrWhiteSpace(s)))
            : "";
        return $"[{id}] vendor: {d?.Vendor?.Value ?? "?"} | date: {IsoDate(d?.Date?.Value)} | " +
               $"total: {total} {d?.Currency?.Value ?? ""} | address: {d?.Address?.Value ?? "?"}{items}";
    }

    private static string IsoDate(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? ""
        : GroundTruth.ParseDate(raw) is { } d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        : raw;
}
