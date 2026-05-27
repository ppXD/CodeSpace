namespace CodeSpace.Core.Services.Workflows.Engine;

/// <summary>
/// Thrown by <see cref="MissingProjectRefValidator"/> when running in
/// <see cref="Hardening.EnforcementMode.Strict"/> and a workflow definition references a
/// project slug that does not exist (or was soft-deleted) in the current team.
///
/// <para>The bug this guards against: pre-fix, the engine silently dropped missing slugs
/// from the per-run project bag, and <c>VariableResolver.WalkProjectsScope</c> resolved
/// the missing refs to <c>null</c>. The run "succeeded" with corrupted data — node
/// outputs and Terminal payloads contained empty strings where the operator expected
/// values. Strict mode throws this exception so the run lands in <c>Failed</c> with a
/// clear cause instead.</para>
///
/// <para>Engine catches <see cref="Exception"/> from scope-build and writes the message
/// to <c>WorkflowRun.Error</c> — the operator sees this text on the run-detail page.</para>
/// </summary>
public sealed class MissingProjectRefException : Exception
{
    public MissingProjectRefException(string message) : base(message)
    {
    }
}
