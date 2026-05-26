namespace CodeSpace.Core.Services.Workflows;

/// <summary>
/// Thrown when a workflow definition fails <see cref="Engine.DefinitionValidator"/> checks
/// (unknown TypeKey, dangling edge, cycle, malformed {{ref}} path, etc.). Distinct from
/// <see cref="InvalidOperationException"/> so the global exception filter can map this to
/// 422 Unprocessable Entity — "the request is syntactically valid JSON but semantically
/// rejected by domain rules" — instead of the catch-all 400. The frontend's editor branches
/// on the HTTP status to render the inline validation banner with every error at once.
///
/// <para>Errors are kept as the raw string list so the UI can render one row per issue,
/// matching how the validator reports them. <see cref="Exception.Message"/> joins them for
/// log/telemetry consumers that only see the single message field.</para>
/// </summary>
public sealed class WorkflowValidationException : Exception
{
    public IReadOnlyList<string> Errors { get; }

    public WorkflowValidationException(IReadOnlyList<string> errors)
        : base("Workflow definition is invalid: " + string.Join("; ", errors))
    {
        Errors = errors;
    }
}
