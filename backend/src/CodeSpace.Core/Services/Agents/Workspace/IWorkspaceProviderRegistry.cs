namespace CodeSpace.Core.Services.Agents.Workspace;

/// <summary>
/// Resolves an <see cref="IWorkspaceProvider"/> by its <see cref="IWorkspaceProvider.Kind"/>. Same shape
/// as <c>ISandboxRunnerRegistry</c>: the policy that decides which provider an agent run uses (it pairs
/// with the chosen runner kind) lives in the caller; this registry only maps a kind to its
/// implementation. A new provider becomes resolvable by registering its class — no edit here.
/// </summary>
public interface IWorkspaceProviderRegistry
{
    /// <summary>Every registered provider, for diagnostics / "which backends are available" surfaces.</summary>
    IReadOnlyList<IWorkspaceProvider> All { get; }

    /// <summary>Resolve the provider for <paramref name="kind"/>. Throws when none is registered for that kind.</summary>
    IWorkspaceProvider Resolve(string kind);
}
