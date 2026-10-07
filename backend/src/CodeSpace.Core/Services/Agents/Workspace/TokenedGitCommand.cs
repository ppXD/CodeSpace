using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Workspace;

/// <summary>
/// The one way a platform <c>git</c> command carries a clone credential. The command names its remote by a URL without
/// userinfo (<see cref="Remote.Url"/>) and carries the credential in its environment, where only a credential helper
/// scoped to that remote reads it. A token in the URL would sit in the argv of git and of every transport child, which any
/// host user reads from <c>/proc/&lt;pid&gt;/cmdline</c> unless <c>/proc</c> hides other users' processes; in the
/// <c>.git/config</c> a clone writes before its transfer starts; and in the hands of any authority the remote redirects to,
/// since git answers its 401 with the URL's credential. The environment is readable by the same uid and root alone.
///
/// <para><see cref="CredentialEnvironment"/> hands git its config through <c>GIT_CONFIG_COUNT</c>, which git reads after
/// system, global and repository config and every child inherits — the transport, git-lfs, the <c>git credential</c> that
/// git-lfs runs. Its first entry empties the helper list for the remote's scheme and authority: an empty value clears the
/// helpers collected so far, URL-scoped, path-scoped and included ones too, so no operator helper is asked for this
/// remote's credential, or told to <c>store</c> it on success or <c>erase</c> it on a refusal. Its second names
/// <see cref="CredentialHelper"/>, which answers <c>get</c> from <see cref="UsernameVariable"/> and
/// <see cref="PasswordVariable"/> with the shell's builtin printf, so the values never reach an argv either. Both are scoped
/// rather than global: git asks the same helpers for other hosts' credentials — an authenticating proxy named with only a
/// user, a separate LFS host — and those must still answer. A redirect to another authority finds no helper that answers
/// for it, so the command fails rather than send the credential there. <c>GIT_CONFIG_COUNT</c> needs git 2.31 or later; an
/// older git ignores it, finds no credential and fails the command rather than leak it.</para>
///
/// <para>The helper stops answering once the remote has refused the credential. git-lfs answers a 401 by telling the
/// helpers to <c>erase</c> the credential and asking them again, with no limit, so a helper that always answered would keep
/// it retrying a refused token — one past its lifetime, a revoked one — for the whole command timeout, tens of failed logins
/// a second. The helper records the <c>erase</c> as an empty marker file in the directory <see cref="StateVariable"/> names,
/// which the runner makes owner-only for this one command (<see cref="SandboxSpec.ConfigHomeEnvVars"/>), binds into a
/// confined one — as its <c>HOME</c> too, as empty as the private <c>/tmp</c> it had — and removes afterwards; after that a
/// <c>get</c> answers nothing and says on stderr that the remote refused it, so git-lfs fails at once with that reason.
/// Without the directory the helper never answers: it could not stop the retries. git itself stops after one
/// refusal.</para>
///
/// <para>The third and fourth entries map the remote's URL to itself as a <c>url.&lt;base&gt;.insteadOf</c> and
/// <c>pushInsteadOf</c>. git and git-lfs rewrite a URL by the longest matching prefix, and the whole URL is the longest any
/// rule can match, so no operator rule moves a tokened command: not <c>url."git@host:".insteadOf=https://host/</c> to SSH
/// under the host's key, nor a rule carrying an operator's own token in place of the one this command is bound to. No rule
/// matched a URL with the token in it, so this keeps what tokened commands always did. The last two turn auto-gc and
/// auto-maintenance off, so no detached gc or maintenance child outlives the command with the credential in its
/// environment.</para>
///
/// <para>No <c>-c</c> on a tokened command may touch the credential helpers: git reads <c>GIT_CONFIG_PARAMETERS</c> after
/// <c>GIT_CONFIG_COUNT</c>, so a <c>-c credential.&lt;url&gt;.helper=</c> would empty the list again and the command would
/// find no credential. The two variables are not <c>GIT_</c>-prefixed: git-lfs writes every <c>GIT_*</c> variable into the
/// logs it keeps inside the clone.</para>
///
/// <para>git also writes every command's argv, each child's, and the variables an operator's <c>trace2.envVars</c> names,
/// to the trace2 targets in system and global config, which are read before any command-line or environment config; a
/// tokened command runs with <see cref="TraceOff"/> in its environment, which wins over them. An untokened command is left
/// as written: the operator's helpers may be how it authenticates to a private mirror.</para>
/// </summary>
internal static class TokenedGitCommand
{
    /// <summary>The variable <see cref="CredentialHelper"/> reads the username from.</summary>
    internal const string UsernameVariable = "CODESPACE_GIT_USERNAME";

    /// <summary>The variable <see cref="CredentialHelper"/> reads the password — the token — from.</summary>
    internal const string PasswordVariable = "CODESPACE_GIT_PASSWORD";

    /// <summary>The variable the runner points at the command's own owner-only directory, where <see cref="CredentialHelper"/> records that the remote refused the credential.</summary>
    internal const string StateVariable = "CODESPACE_GIT_STATE";

    /// <summary>The username a token is sent under when its provider names none: GitHub's.</summary>
    internal const string DefaultTokenUsername = "x-access-token";

    /// <summary>
    /// The credential helper a tokened command names: a shell function that answers <c>get</c> from the two variables until
    /// an <c>erase</c> records in the <see cref="StateVariable"/> directory that the remote refused the credential, then says
    /// so on stderr instead; it ignores <c>store</c>, and answers nothing without that directory.
    /// </summary>
    internal const string CredentialHelper = """!f() { r="$CODESPACE_GIT_STATE/refused"; case "$1" in get) if test -e "$r"; then echo "the remote refused this credential; not offering it again" >&2; elif test -d "$CODESPACE_GIT_STATE"; then printf "username=%s\npassword=%s\n" "$CODESPACE_GIT_USERNAME" "$CODESPACE_GIT_PASSWORD"; fi;; erase) test -d "$CODESPACE_GIT_STATE" && : > "$r";; esac; }; f""";

    /// <summary>The environment a tokened command runs with besides its credential: git's three trace2 targets switched off.</summary>
    internal static readonly IReadOnlyDictionary<string, string> TraceOff = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["GIT_TRACE2"] = "0",
        ["GIT_TRACE2_EVENT"] = "0",
        ["GIT_TRACE2_PERF"] = "0",
    };

    /// <summary>A remote as a git command names it — <see cref="Url"/>, with no userinfo secret — and the credential that goes with it, if any.</summary>
    internal sealed record Remote(string Url, string? Username, string? Password)
    {
        /// <summary>True when the remote carries a credential, so every command that reaches it runs as a tokened command.</summary>
        public bool IsTokened => Password is not null;
    }

    /// <summary>
    /// The remote <paramref name="repositoryUrl"/> names, with the clone <paramref name="token"/> for it. With a token, the URL
    /// loses any userinfo and the token is the credential, under <paramref name="tokenUsername"/> (<see cref="DefaultTokenUsername"/>
    /// when the provider names none). Without one, an http(s) URL whose userinfo carries a password — a stored credential —
    /// gives it up the same way; any other URL is left as written, a bare username included: it names an account, and the
    /// operator's helpers may answer for it.
    /// </summary>
    internal static Remote RemoteFor(string repositoryUrl, string? tokenUsername, string? token)
    {
        if (!string.IsNullOrEmpty(token)) return new Remote(WithoutUserInfo(repositoryUrl), string.IsNullOrEmpty(tokenUsername) ? DefaultTokenUsername : tokenUsername, token);

        return HttpUserInfo(repositoryUrl) is [_, { Length: > 0 }] ? FromUserInfo(repositoryUrl) : new Remote(repositoryUrl, null, null);
    }

    /// <summary>
    /// The remote an http(s) <paramref name="url"/> names, its whole userinfo moved into the credential, decoded as git decodes
    /// it: the user, and the password or an empty one — a token pasted as the user alone is sent with an empty password, as
    /// curl sent it from the URL. For a caller that knows the URL's userinfo is a credential even without a password.
    /// </summary>
    internal static Remote FromUserInfo(string url)
    {
        var parts = HttpUserInfo(url) ?? throw new ArgumentException("Only an http(s) URL carries a credential a helper can answer for.", nameof(url));

        return new Remote(WithoutUserInfo(url), Uri.UnescapeDataString(parts[0]), parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
    }

    /// <summary>
    /// <paramref name="spec"/> as a tokened command for <paramref name="remote"/> — <see cref="CredentialEnvironment"/> over its
    /// environment and a state directory for <see cref="StateVariable"/>, its argv untouched — when the remote is tokened,
    /// otherwise unchanged.
    /// </summary>
    internal static SandboxSpec Spec(Remote remote, SandboxSpec spec)
    {
        if (!remote.IsTokened) return spec;

        var environment = new Dictionary<string, string>(spec.Environment);
        foreach (var (name, value) in CredentialEnvironment(remote)) environment[name] = value;

        return spec with { Environment = environment, ConfigHomeEnvVars = [.. spec.ConfigHomeEnvVars, StateVariable] };
    }

    /// <summary>
    /// The environment that carries <paramref name="remote"/>'s credential: the config entries (the scoped reset, then
    /// <see cref="CredentialHelper"/> for the same scope, then the remote's URL mapped to itself for fetch and push, then
    /// auto-gc and auto-maintenance off), the credential in the two variables the helper reads, and <see cref="TraceOff"/>.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> CredentialEnvironment(Remote remote)
    {
        var helperKey = $"credential.{Scope(remote.Url)}.helper";
        var config = new (string Key, string Value)[]
        {
            (helperKey, ""), (helperKey, CredentialHelper),
            ($"url.{remote.Url}.insteadOf", remote.Url), ($"url.{remote.Url}.pushInsteadOf", remote.Url),
            ("gc.auto", "0"), ("maintenance.auto", "false"),
        };

        var environment = new Dictionary<string, string>(TraceOff, StringComparer.Ordinal) { ["GIT_CONFIG_COUNT"] = config.Length.ToString(), [UsernameVariable] = remote.Username ?? "", [PasswordVariable] = remote.Password ?? "" };

        for (var i = 0; i < config.Length; i++)
        {
            environment[$"GIT_CONFIG_KEY_{i}"] = config[i].Key;
            environment[$"GIT_CONFIG_VALUE_{i}"] = config[i].Value;
        }

        return environment;
    }

    /// <summary>The scheme and authority a credential is answered for — never a path, never a userinfo.</summary>
    private static string Scope(string url)
    {
        var uri = new Uri(url);

        return $"{uri.Scheme}://{uri.Authority}";
    }

    /// <summary><paramref name="url"/> without its userinfo, or as written when it has none.</summary>
    private static string WithoutUserInfo(string url) => RemoteTipResolver.SanitizeUrl(url);

    /// <summary>An http(s) URL's userinfo split at its first ':' — the user, then the password when there is one — still encoded; null for any other URL or one without userinfo.</summary>
    private static string[]? HttpUserInfo(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) && uri.UserInfo.Length > 0 ? uri.UserInfo.Split(':', 2) : null;
}
