using System.ComponentModel;
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
/// Returned lines are matched by EXACT full ref name (ls-remote patterns are tail-matched globs — a pattern hit is
/// necessary but not sufficient), so a glob-shaped or shadowing ref can never pin the wrong commit.
/// </summary>
public sealed class RemoteTipResolver : IRemoteTipResolver, ISingletonDependency
{
    /// <summary>Deliberately short: this runs on the synchronous launch path (inside the request's transaction) — a slow remote must fail the launch fast, not hold the connection for a minute.</summary>
    private const int LsRemoteTimeoutSeconds = 15;

    private readonly ISandboxRunnerRegistry _runners;

    public RemoteTipResolver(ISandboxRunnerRegistry runners) { _runners = runners; }

    public async Task<string?> ResolveTipShaAsync(WorkspaceRequest request, bool refRequired, CancellationToken cancellationToken)
    {
        var url = LocalGitWorkspaceProvider.BuildAuthenticatedUrl(request.RepositoryUrl, request.TokenUsername, request.Token);

        if (string.IsNullOrWhiteSpace(request.Ref)) return await ResolveHeadAsync(url, request, cancellationToken).ConfigureAwait(false);

        if (await ResolveRefAsync(url, request.Ref!, request, cancellationToken).ConfigureAwait(false) is { } sha) return sha;

        // The request's own SOFT semantics (a session-inherited prior branch that a merged PR may have pruned):
        // fall to the default branch, mirroring the clone's ResolveCheckoutRefAsync. A HARD ref (DefaultRef null)
        // that is gone fails LOUD — the clone would fail identically later; the pin just surfaces it at launch.
        if (!string.IsNullOrWhiteSpace(request.DefaultRef) && !string.Equals(request.Ref, request.DefaultRef, StringComparison.Ordinal))
        {
            if (await ResolveRefAsync(url, request.DefaultRef!, request, cancellationToken).ConfigureAwait(false) is { } fallback) return fallback;

            if (refRequired) throw MissingRef(request.DefaultRef!, request);

            return null;
        }

        if (refRequired) throw MissingRef(request.Ref!, request);

        // The caller's ref was IMPLICIT (a recorded default branch, not an operator/authored pin) — a remote that
        // doesn't have it (an empty just-created repo, a stale default-branch record) launches UNPINNED, exactly
        // the pre-S1 behaviour, instead of turning an opportunistic pin into a launch failure.
        return null;
    }

    /// <summary>The remote's HEAD commit — null for an EMPTY remote (ls-remote succeeds with no output: nothing exists to pin).</summary>
    private async Task<string?> ResolveHeadAsync(string url, WorkspaceRequest request, CancellationToken cancellationToken)
    {
        var lines = await LsRemoteAsync(url, new[] { "HEAD" }, request, cancellationToken).ConfigureAwait(false);

        return lines.Where(l => l.Ref == "HEAD").Select(l => l.Sha).FirstOrDefault();
    }

    /// <summary>The tip commit of a NAMED ref: its branch, else its tag (peeled <c>^{{}}</c> commit preferred over the annotated tag object). Null when the remote has no such ref. Lines are matched by EXACT full ref name, never by the pattern's tail-glob.</summary>
    private async Task<string?> ResolveRefAsync(string url, string @ref, WorkspaceRequest request, CancellationToken cancellationToken)
    {
        var branch = await LsRemoteAsync(url, new[] { $"refs/heads/{@ref}" }, request, cancellationToken).ConfigureAwait(false);

        if (branch.FirstOrDefault(l => l.Ref == $"refs/heads/{@ref}") is { Sha.Length: > 0 } hit) return hit.Sha;

        var tags = await LsRemoteAsync(url, new[] { $"refs/tags/{@ref}", $"refs/tags/{@ref}^{{}}" }, request, cancellationToken).ConfigureAwait(false);

        return tags.Where(t => t.Ref == $"refs/tags/{@ref}^{{}}").Select(t => t.Sha).FirstOrDefault()
            ?? tags.Where(t => t.Ref == $"refs/tags/{@ref}").Select(t => t.Sha).FirstOrDefault();
    }

    /// <summary>One <c>git ls-remote</c> round-trip parsed to (sha, ref) lines. A non-zero exit throws LOUD with the token redacted and the URL stripped of any userinfo — an unreachable remote at launch is the SAME failure the clone would surface later, just earlier and honest.</summary>
    private async Task<IReadOnlyList<(string Sha, string Ref)>> LsRemoteAsync(string url, IReadOnlyList<string> patterns, WorkspaceRequest request, CancellationToken cancellationToken)
    {
        var args = new List<string> { "ls-remote", url };
        args.AddRange(patterns);

        SandboxResult result;
        try
        {
            result = await _runners.Resolve(LocalProcessRunner.LocalKind)
                .RunAsync(new SandboxSpec { Command = "git", Args = args, TimeoutSeconds = LsRemoteTimeoutSeconds }, cancellationToken).ConfigureAwait(false);
        }
        catch (Win32Exception ex)
        {
            // The git binary itself is absent — a deployment-topology bug, not a remote fault. Name it directly:
            // this resolver is the ONE sanctioned synchronous git use, and the API image must carry git for it.
            throw new WorkspaceException($"git is not available on this host — launch-time base resolution runs `git ls-remote` on the API pod, so its image must include git (see Dockerfile.api): {ex.Message}");
        }

        if (result.Status != SandboxStatus.Success)
            throw new WorkspaceException($"git ls-remote failed (exit {result.ExitCode}) resolving the launch base for {SanitizeUrl(request.RepositoryUrl)}: {LocalGitWorkspaceProvider.Redact(Truncate(result.Stderr), request.Token)} — the launch base pin requires a reachable remote; the clone would fail the same way later");

        // sha1 (40) and sha256 (64) object formats both count — a sha256 remote must not read as "ref missing".
        return result.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('\t'))
            .Where(parts => parts.Length == 2 && parts[0].Length is 40 or 64)
            .Select(parts => (parts[0], parts[1]))
            .ToList();
    }

    private static WorkspaceException MissingRef(string @ref, WorkspaceRequest request) =>
        new($"the launch base ref '{@ref}' does not exist on {SanitizeUrl(request.RepositoryUrl)} — the launch base pin is resolved from the ref the run would clone, so a missing ref fails the launch loud instead of silently launching unpinned");

    /// <summary>Strip any userinfo from a URL before it enters an exception message — a stored clone URL may itself carry credentials the token-based <see cref="LocalGitWorkspaceProvider.Redact"/> knows nothing about.</summary>
    internal static string SanitizeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || string.IsNullOrEmpty(parsed.UserInfo)) return url;

        return new UriBuilder(parsed) { UserName = "", Password = "" }.Uri.AbsoluteUri;
    }

    private static string Truncate(string text) => text.Length <= 500 ? text : text[..500];
}
