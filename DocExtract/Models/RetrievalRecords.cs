namespace DocExtract.Models;

/// <summary>
/// One retrieval question and its ground truth. <paramref name="RelevantDocIds"/> and
/// <paramref name="ExpectedValue"/> are both computed from the SROIE keys — never hand-written
/// and never model-written. That is the whole point: a retrieval score is only worth reading
/// if the thing it is scored against was not produced by the system under test.
/// </summary>
public sealed record RetrievalQuestion(
    string Id,
    string Kind,
    string Text,
    List<string> RelevantDocIds,
    double ExpectedValue,
    string ExpectedUnit);

/// <summary>One indexed artifact: the doc ID plus the text the retriever actually searches.</summary>
public sealed record IndexedDoc(string DocId, string Text, string Summary);

/// <summary>A retrieved candidate with the score that ranked it.</summary>
public sealed record Hit(string DocId, double Score);

/// <summary>
/// What the model returned for one question. Nothing here is trusted: the citation list is
/// checked against what was actually retrieved and the figure against ground truth, both
/// deterministically, before any of it counts as grounded.
/// </summary>
public sealed record AnswerPayload(double? Answer, List<string>? CitedDocIds);

/// <summary>Per-question outcome: what was retrieved, what was answered, what held up.</summary>
public sealed record QuestionOutcome(
    string QuestionId,
    string Kind,
    int FirstRelevantRank,
    Dictionary<int, double> RecallAtK,
    bool AnswerAttempted,
    bool Answered,
    bool Cited,
    bool CitationsInContext,
    bool FigureMatchesGt,
    string? Note);
