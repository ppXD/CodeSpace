using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Agents.Workspace.Providers;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Workspace;

/// <summary>
/// <see cref="IRemoteTipResolver"/> over <c>git ls-remote</c>, run through the local <see cref="ISandboxRunner"/>
/// exactly like <see cref="Providers.LocalGitWorkspaceProvider"/>'s own git calls (same auth-URL embedding, same
/// token redaction on surfaced errors, same process/timeout handling). Branch first, tag second (preferring the
/// peeled <c>^{}</c> commit over the annotated tag object — the pin is a COMMIT), HEAD when no ref is named.
/// </summary>
public sealed class RemoteTipResolver : IRemoteTipResolver, ISingletonDependency
{
    private const int LsRemoteTimeoutSeconds = 60;

    private readonly ISandboxRunnerRegistry _runners;

    public RemoteTipResolver(ISandboxRunnerRegistry runners) { _runners = runners; }

    public async Task<string?> ResolveTipShaAsync(WorkspaceRequest request, CancellationToken cancellationToken)
    {
        var url = LocalGitWorkspaceProvider.BuildAuthenticatedUrl(request.RepositoryUrl, request.TokenUsername, request.Token);

        if (string.IsNullOrWhiteSpace(request.Ref)) return await ResolveHeadAsync(url, request, cancellationToken).ConfigureAwait(false);

        if (await ResolveRefAsync(url, request.Ref!, request, cancellationToken).ConfigureAwait(false) is { } sha) return sha;

        // The request's own SOFT semantics (a session-inherited prior branch that a merged PR may have pruned):
        // fall to the default branch, mirroring the clone's ResolveCheckoutRefAsync. A HARD ref (DefaultRef null)
        // that is gone fails LOUD — the clone would fail identically later; the pin just surfaces it at launch.
        if (!string.IsNullOrWhiteSpace(request.DefaultRef) && !string.Equals(request.Ref, request.DefaultRef, StringComparison.Ordinal))
            return await ResolveRefAsync(url, request.DefaultRef!, request, cancellationToken).ConfigureAwait(false)
                ?? throw MissingRef(request.DefaultRef!, request);

        throw MissingRef(request.Ref!, request);
    }

    /// <summary>The remote's HEAD commit — null for an EMPTY remote (ls-remote succeeds with no output: nothing exists to pin).</summary>
    private async Task<string?> ResolveHeadAsync(string url, WorkspaceRequest request, CancellationToken cancellationToken)
    {
        var lines = await LsRemoteAsync(url, new[] { "HEAD" }, request, cancellationToken).ConfigureAwait(false);

        return lines.Where(l => l.Ref == "HEAD").Select(l => l.Sha).FirstOrDefault();
    }

    /// <summary>The tip commit of a NAMED ref: its branch, else its tag (peeled <c>^{{}}</c> commit preferred over the annotated tag object). Null when the remote has no such ref.</summary>
    private async Task<string?> ResolveRefAsync(string url, string @ref, WorkspaceRequest request, CancellationToken cancellationToken)
    {
        var branch = await LsRemoteAsync(url, new[] { $"refs/heads/{@ref}" }, request, cancellationToken).ConfigureAwait(false);

        if (branch.Count > 0) return branch[0].Sha;

        var tags = await LsRemoteAsync(url, new[] { $"refs/tags/{@ref}", $"refs/tags/{@ref}^{{}}" }, request, cancellationToken).ConfigureAwait(false);

        return tags.Where(t => t.Ref.EndsWith("^{}", StringComparison.Ordinal)).Select(t => t.Sha).FirstOrDefault()
            ?? tags.Select(t => t.Sha).FirstOrDefault();
    }

    /// <summary>One <c>git ls-remote</c> round-trip parsed to (sha, ref) lines. A non-zero exit throws LOUD with the token redacted — an unreachable remote at launch is the SAME failure the clone would surface later, just earlier and honest.</summary>
    private async Task<IReadOnlyList<(string Sha, string Ref)>> LsRemoteAsync(string url, IReadOnlyList<string> patterns, WorkspaceRequest request, CancellationToken cancellationToken)
    {
        var args = new List<string> { "ls-remote", url };
        args.AddRange(patterns);

        var result = await _runners.Resolve(LocalProcessRunner.LocalKind)
            .RunAsync(new SandboxSpec { Command = "git", Args = args, TimeoutSeconds = LsRemoteTimeoutSeconds }, cancellationToken).ConfigureAwait(false);

        if (result.Status != SandboxStatus.Success)
            throw new WorkspaceException($"git ls-remote failed (exit {result.ExitCode}) resolving the launch base for {request.RepositoryUrl}: {LocalGitWorkspaceProvider.Redact(Truncate(result.Stderr), request.Token)} — the launch base pin requires a reachable remote; the clone would fail the same way later");

        return result.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('\t'))
            .Where(parts => parts.Length == 2 && parts[0].Length == 40)
            .Select(parts => (parts[0], parts[1]))
            .ToList();
    }

    private static WorkspaceException MissingRef(string @ref, WorkspaceRequest request) =>
        new($"the launch base ref '{@ref}' does not exist on {request.RepositoryUrl} — the launch base pin is resolved from the ref the run would clone, so a missing ref fails the launch loud instead of silently launching unpinned");

    private static string Truncate(string text) => text.Length <= 500 ? text : text[..500];
}
