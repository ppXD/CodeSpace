namespace CodeSpace.Messages.Dtos.Sessions.Journal;

/// <summary>
/// The full, on-demand detail of ONE model call — what the drawer shows when the operator opens a model-call row. Fetched
/// lazily (not carried in the journal projection, which would bloat every poll) from the call's <c>interaction.started</c>
/// (the prompt) + <c>interaction.completed</c>/<c>failed</c> (the result + usage) ledger records, keyed by the completed
/// record's sequence. Offloaded fields (a large prompt / result moved to a content-addressed artifact) are resolved back
/// to text here, so the drawer reads them whole. Each field is a display string (pretty JSON or plain text); null when the
/// call didn't carry it.
/// </summary>
public sealed record ModelCallDetail
{
    /// <summary>The prompt the call was given — its system + user content, offloaded parts resolved. Null when the start wasn't found / carried none.</summary>
    public string? Prompt { get; init; }

    /// <summary>The model's output (the structured completion / decision), offloaded parts resolved. Null on a failed call that produced none.</summary>
    public string? Result { get; init; }

    /// <summary>The token usage (input / output / finish reason) as pretty JSON. Null when the call reported none.</summary>
    public string? Usage { get; init; }

    /// <summary>The raw ledger records (the started + completed payloads) as pretty JSON — the audit trace, with any offload refs left visible.</summary>
    public required string Trace { get; init; }
}
