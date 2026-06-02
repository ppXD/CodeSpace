namespace CodeSpace.Core.Services.Workflows.Engine;

/// <summary>
/// Thrown by <see cref="MissingRequiredInputValidator"/> when a workflow definition declares
/// an input as <c>Required = true</c> that neither the manual-run / webhook trigger payload
/// supplied nor the definition's <c>Default</c> filled in.
///
/// <para>The bug class this guards against: pre-validator, the engine silently omitted the
/// missing key from the <c>{{input.*}}</c> bag and downstream nodes received
/// <c>JsonValueKind.Null</c> when they templated <c>{{input.required_name}}</c>. The run
/// "succeeded" but the missing value cascaded into node outputs (empty PR comment bodies,
/// blank LLM prompts, etc.). The validator throws this so the run lands in
/// <c>WorkflowRunStatus.Failure</c> with a clear cause instead.</para>
///
/// <para>Engine catches <see cref="Exception"/> from scope-build and writes the message
/// to <c>WorkflowRun.Error</c> — the operator sees this text on the run-detail page.</para>
/// </summary>
public sealed class MissingRequiredInputException : Exception
{
    public MissingRequiredInputException(string message) : base(message)
    {
    }
}
