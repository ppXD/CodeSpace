namespace CodeSpace.Core.Services.Workflows.Engine;

/// <summary>
/// Thrown when a Terminal node's output mapping references a Secret-typed variable (e.g.
/// <c>{{team.API_KEY}}</c> in a Terminal's <c>inputs</c> JSON). The engine catches this in
/// <c>ExecuteRunAsync</c> and lands the run in <c>Failure</c> status with the exception
/// message as the run error, so operators see a clear error in the run-detail UI.
///
/// <para>Separate from <c>NodeFailureException</c> on purpose — this is a contract
/// violation (don't put secrets in outputs), not a runtime problem. Future tooling can
/// surface "contract violations" distinctly from "runtime errors" without re-parsing
/// the message.</para>
///
/// <para>The guard runs at scope build → Terminal-output capture time. Secrets in node
/// Config / Inputs (consumed in-process by HttpRequestNode, LlmCompleteNode, etc.) are
/// fine — they cross the wire to whatever provider but are never persisted to
/// <c>workflow_run.OutputsJson</c>. Only Terminal-output mappings are checked.</para>
/// </summary>
public sealed class WorkflowSecretLeakException : Exception
{
    public WorkflowSecretLeakException(string message) : base(message) { }
}
