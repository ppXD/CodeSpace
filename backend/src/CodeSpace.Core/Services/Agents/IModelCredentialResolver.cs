using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// Resolves the model credential an agent run authenticates with — the "which key does this run use" seam,
/// invoked just-in-time in the executor (NOT at staging, so the secret is never frozen into <c>TaskJson</c>).
/// Mirrors <see cref="Workspace.IAgentWorkspaceResolver"/>: given the run's task + its team (taken from the
/// loaded run row, never the forgeable envelope), it decrypts and returns a transient
/// <see cref="ResolvedModelCredential"/>, or <c>null</c> when no credential applies (the run relies on
/// whatever env the runner already provides).
///
/// <para>Precedence: an explicitly pinned <c>task.ModelCredentialId</c> (node override or persona default,
/// already merged) MUST resolve to an Active, non-deleted credential in THIS team or fail — never silently
/// fall back to a different credential (a confused-deputy). With no pin: the team's most-recent Active
/// credential for one of the harness's providers, else an operator-global single-tenant key, else null.</para>
/// </summary>
public interface IModelCredentialResolver
{
    /// <summary>
    /// Resolve the run's model credential, or <c>null</c> when none applies. <paramref name="projector"/> (the
    /// harness, when it can authenticate) bounds which providers a team-default / operator-global key may use.
    /// Throws <see cref="ModelCredentialResolutionException"/> when a PINNED credential can't be resolved for
    /// the team (missing / foreign-team / revoked / deleted) — a clean failure, never a fall-through.
    /// </summary>
    Task<ResolvedModelCredential?> ResolveAsync(AgentTask task, Guid teamId, IModelCredentialProjector? projector, CancellationToken cancellationToken);
}

/// <summary>A pinned model credential could not be resolved for the run's team (absent, cross-team, revoked, or deleted). The executor surfaces this as a clean run failure — never a fall-through to a different key.</summary>
public sealed class ModelCredentialResolutionException : Exception
{
    public ModelCredentialResolutionException(string message) : base(message) { }
}
