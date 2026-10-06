using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Workspace;

/// <summary>
/// Marks a <c>git</c> command as TOKENED — one whose git transport can reach a remote whose URL carries a clone token,
/// either named in its own argv (the clone, a probe or readback <c>ls-remote</c>, the publish <c>push</c>, and
/// <c>lfs push</c>, which runs git's transport against that URL itself) or as the <c>origin</c> of the clone it runs in
/// (a fetch or push before the token is stripped) — and keeps that token out of the operator's credential helpers and
/// trace2 targets.
///
/// <para>The token is already in the URL, so a tokened command has nothing to ask a helper for. But git still honours
/// the helpers in system and global config, and on success its transport hands the URL's username and password to each
/// of them to <c>store</c>: an operator's <c>credential.helper=store</c> (or <c>cache</c>, or a keychain) would keep
/// every run's token at rest. <see cref="CredentialHelperReset"/> empties the helper list for the tokened remote's scheme
/// and authority: an empty value clears the helpers collected so far, URL-scoped, path-scoped, user-scoped and included
/// ones too, and a command-line value is read after system, global and repository config. It reaches child processes
/// (git-lfs, the git transport <c>lfs push</c> runs) through <c>GIT_CONFIG_PARAMETERS</c>. It is scoped rather than
/// global because git asks the same helpers for other hosts' credentials — an authenticating proxy named with only a
/// user, a separate LFS host — and those must still answer.</para>
///
/// <para>git also writes every command's argv, and each child's (<c>git remote-http &lt;url&gt;</c>), to the trace2
/// targets in system and global config; git 2.33 writes the URL's password verbatim. Those targets are read before any
/// <c>-c</c>, so a tokened command runs with <see cref="TraceOff"/> in its environment, which wins over them.</para>
///
/// <para>git-lfs's own object transfers through a tokened origin — the downloads a checkout, a hard reset or an apply
/// makes — authenticate from the URL's userinfo and neither ask nor tell a helper (verified with git-lfs 3.7.1), so a
/// command that only reaches the origin that way is not tokened. An untokened command is left as written: the
/// operator's helpers may be how it authenticates to a private mirror.</para>
/// </summary>
internal static class TokenedGitCommand
{
    /// <summary>The environment a tokened command runs with: git's three trace2 targets switched off.</summary>
    internal static readonly IReadOnlyDictionary<string, string> TraceOff = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["GIT_TRACE2"] = "0",
        ["GIT_TRACE2_EVENT"] = "0",
        ["GIT_TRACE2_PERF"] = "0",
    };

    /// <summary>True when <paramref name="remoteUrl"/> embeds a password: the token <c>LocalGitWorkspaceProvider.BuildAuthenticatedUrl</c> put there, or one a stored or pasted URL already carries. A bare username is not a secret, and a path or an scp-style address carries none.</summary>
    internal static bool IsTokened(string remoteUrl) =>
        Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri) && uri.UserInfo.Split(':', 2) is [_, { Length: > 0 }];

    /// <summary>The config a tokened command gets ahead of its own arguments: an empty helper list for the remote's scheme and authority — never its userinfo, so the key carries no token. Empty, not <c>false</c>: only the empty value resets the list; any other value adds one more helper.</summary>
    internal static IReadOnlyList<string> CredentialHelperReset(string remoteUrl)
    {
        var uri = new Uri(remoteUrl);

        return new[] { "-c", $"credential.{uri.Scheme}://{uri.Authority}.helper=" };
    }

    /// <summary><paramref name="spec"/> as a tokened command (<see cref="AsTokened"/>) when the remote it can reach is tokened, otherwise unchanged.</summary>
    internal static SandboxSpec Spec(string remoteUrl, SandboxSpec spec) => IsTokened(remoteUrl) ? AsTokened(remoteUrl, spec) : spec;

    /// <summary>
    /// <paramref name="spec"/> as a tokened command for <paramref name="remoteUrl"/> — <see cref="CredentialHelperReset"/> ahead of
    /// its arguments, <see cref="TraceOff"/> over its environment — whatever <see cref="IsTokened"/> says: for a caller that knows
    /// the URL's userinfo is a credential without a password, as a token pasted as the user alone is.
    /// </summary>
    internal static SandboxSpec AsTokened(string remoteUrl, SandboxSpec spec)
    {
        var environment = new Dictionary<string, string>(spec.Environment);
        foreach (var (name, value) in TraceOff) environment[name] = value;

        return spec with { Args = [.. CredentialHelperReset(remoteUrl), .. spec.Args], Environment = environment };
    }
}
