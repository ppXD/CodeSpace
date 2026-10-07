using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// <see cref="TokenedGitCommand"/> — the one way a platform git command carries a clone credential. Pins literally the
/// variables and the credential helper, the scoped config, remote pin and trace2 switch the environment carries, the state
/// directory the runner makes for it, which remotes are tokened and how their URL sheds its userinfo; runs the helper
/// through a real shell. The call sites are pinned next to their own classes, and <c>TokenedGitCredentialHelperFlowTests</c>
/// proves the whole against real git and git-lfs, a real store helper, a proxy whose password that helper holds, a remote
/// that redirects to a harvester, a remote that refuses the token, operator URL rewrites, and real trace2 targets.
/// </summary>
[Trait("Category", "Unit")]
public sealed class TokenedGitCommandTests
{
    [Fact]
    public void The_credential_variables_and_the_helper_are_pinned_literally()
    {
        // The helper reads the credential from these two variables — never GIT_-prefixed, since git-lfs writes every GIT_*
        // variable into its own logs inside the clone — and writes it with the shell's builtin printf, so the values never
        // reach an argv. It answers get until git or git-lfs tells it, by erase, that the remote refused the credential; it
        // records that in the command's own state directory, which the runner makes and removes, and answers nothing after.
        TokenedGitCommand.UsernameVariable.ShouldBe("CODESPACE_GIT_USERNAME");
        TokenedGitCommand.PasswordVariable.ShouldBe("CODESPACE_GIT_PASSWORD");
        TokenedGitCommand.StateVariable.ShouldBe(TokenedGitSpecs.StateVariable);
        TokenedGitCommand.CredentialHelper.ShouldBe(TokenedGitSpecs.Helper);
    }

    [Fact]
    public void Trace2_off_is_pinned_literally()
    {
        // git's own names for its three trace2 targets. The environment wins over the trace2.*Target an operator set in
        // system or global config, which git reads before any -c or environment config.
        TokenedGitCommand.TraceOff.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}").ShouldBe(new[] { "GIT_TRACE2=0", "GIT_TRACE2_EVENT=0", "GIT_TRACE2_PERF=0" });
    }

    [Theory]
    [InlineData("https://github.com/org/repo.git", null, "ghp_abc", "https://github.com", "x-access-token")]                 // GitHub's token user, the default
    [InlineData("https://gitlab.com/org/repo.git", "oauth2", "glpat_xyz", "https://gitlab.com", "oauth2")]                     // GitLab's
    [InlineData("http://127.0.0.1:8080/remote.git", "x-access-token", "t", "http://127.0.0.1:8080", "x-access-token")]         // a non-default port is part of the scope
    [InlineData("https://GitLab.Example.com:8443/org/repo.git", "oauth2", "p@ss/w+rd", "https://gitlab.example.com:8443", "oauth2")]
    public void A_tokened_command_carries_the_credential_in_its_environment_for_the_remotes_scope_alone(string url, string? tokenUsername, string token, string scope, string username)
    {
        // The reset (an EMPTY helper, not false: only the empty value clears the list) and the helper are both scoped to the
        // remote's scheme and authority, never its path and never a userinfo: the operator's helpers for that remote are
        // silenced, the ones a proxy or another host needs still answer, and a redirect to another authority finds no helper
        // that answers with the token. The token rides raw — it is in no URL, so nothing escapes it.
        var remote = TokenedGitCommand.RemoteFor(url, tokenUsername, token);
        var spec = new SandboxSpec { Command = "git", Args = new[] { "ls-remote", remote.Url }, TimeoutSeconds = 30, AllowNetwork = true };

        var tokened = TokenedGitCommand.Spec(remote, spec);

        TokenedGitSpecs.RunsTokened(tokened, remote.Url).ShouldBeTrue();
        TokenedGitSpecs.ConfigFor(remote.Url)["GIT_CONFIG_KEY_1"].ShouldBe($"credential.{scope}.helper", "fixture check: the helper answers for the remote's scheme and authority");
        TokenedGitSpecs.CarriesTheCredential(tokened, username, token).ShouldBeTrue();
    }

    [Fact]
    public void A_tokened_command_maps_its_remote_to_itself_so_no_operator_rewrite_moves_it()
    {
        // git and git-lfs rewrite a URL by the longest url.<base>.insteadOf (or pushInsteadOf) prefix that matches it. A rule
        // naming the whole URL is the longest any rule can be, and it maps the URL to itself: an operator's
        // url."git@host:".insteadOf=https://host/ cannot move a tokened command to SSH under the host's key, nor a rule that
        // carries an operator's own token swap the team's credential for it. Before the token left the URL, no such rule
        // matched a tokened URL; this keeps that.
        var remote = TokenedGitCommand.RemoteFor("https://github.com/org/repo.git", null, "ghp_abc");

        var environment = TokenedGitCommand.Spec(remote, new SandboxSpec { Command = "git", Args = new[] { "push", remote.Url, "b:b" } }).Environment;

        var entries = Enumerable.Range(0, int.Parse(environment["GIT_CONFIG_COUNT"])).Select(i => $"{environment[$"GIT_CONFIG_KEY_{i}"]}={environment[$"GIT_CONFIG_VALUE_{i}"]}").ToList();
        entries.ShouldContain("url.https://github.com/org/repo.git.insteadOf=https://github.com/org/repo.git");
        entries.ShouldContain("url.https://github.com/org/repo.git.pushInsteadOf=https://github.com/org/repo.git");
    }

    [Fact]
    public void A_tokened_command_asks_the_runner_for_its_own_state_directory()
    {
        // The helper records a refusal in a directory only this command sees: the runner makes it owner-only, binds it into a
        // confined command, points the variable at it and removes it afterwards. The command's own requests are kept.
        var remote = TokenedGitCommand.RemoteFor("https://host/r.git", null, "t");
        var spec = new SandboxSpec { Command = "git", Args = new[] { "clone", remote.Url, "/tmp/x" }, ConfigHomeEnvVars = new[] { "OTHER_HOME" } };

        TokenedGitCommand.Spec(remote, spec).ConfigHomeEnvVars.ShouldBe(new[] { "OTHER_HOME", "CODESPACE_GIT_STATE" });
    }

    [Fact]
    public void A_tokened_command_keeps_its_argv_and_everything_else()
    {
        // Nothing about the credential reaches the argv — not even the reset as a -c: git reads GIT_CONFIG_PARAMETERS after
        // GIT_CONFIG_COUNT, so a -c credential.<url>.helper= would empty the list again, the helper with it.
        var remote = TokenedGitCommand.RemoteFor("https://host/r.git", null, "t");
        var spec = new SandboxSpec { Command = "git", Args = new[] { "-c", "lfs.locksverify=false", "lfs", "push", remote.Url, "branch" }, Environment = new Dictionary<string, string> { ["LANG"] = "C", ["GIT_TRACE2"] = "/tmp/trace" }, TimeoutSeconds = 30, AllowNetwork = true };

        var tokened = TokenedGitCommand.Spec(remote, spec);

        tokened.Args.ShouldBe(spec.Args);
        tokened.Environment["LANG"].ShouldBe("C", "the command's own environment is kept");
        tokened.Environment["GIT_TRACE2"].ShouldBe("0", "trace2 off wins over any value the command brought");
        (tokened with { Environment = spec.Environment, ConfigHomeEnvVars = spec.ConfigHomeEnvVars }).ShouldBe(spec, "nothing else about the command changes");
        TokenedGitSpecs.RunsTokened(tokened, "https://host/r.git").ShouldBeTrue();
    }

    [Theory]
    [InlineData("https://github.com/org/repo.git", null, "p@ss/w+rd", "https://github.com/org/repo.git", "x-access-token")]
    [InlineData("https://gitlab.com/org/repo.git", "oauth2", "glpat_xyz", "https://gitlab.com/org/repo.git", "oauth2")]
    [InlineData("https://git.local:8443/org/repo.git", "oauth2", "t", "https://git.local:8443/org/repo.git", "oauth2")]
    [InlineData("https://stale:old-secret@github.com/org/repo.git", null, "fresh", "https://github.com/org/repo.git", "x-access-token")]   // a token replaces a stored credential, which leaves the URL too
    public void With_a_token_the_remote_is_named_without_userinfo(string url, string? tokenUsername, string token, string named, string username)
    {
        var remote = TokenedGitCommand.RemoteFor(url, tokenUsername, token);

        remote.ShouldBe(new TokenedGitCommand.Remote(named, username, token));
        remote.IsTokened.ShouldBeTrue();
    }

    [Theory]
    [InlineData("https://x-access-token:ghp_abc@github.com/org/repo.git", "https://github.com/org/repo.git", "x-access-token", "ghp_abc")]
    [InlineData("https://oauth2:p%40ss%2Fword@gitlab.com/org/repo.git", "https://gitlab.com/org/repo.git", "oauth2", "p@ss/word")]   // decoded, as git decodes it
    [InlineData("http://x-access-token:t@127.0.0.1:8080/remote.git", "http://127.0.0.1:8080/remote.git", "x-access-token", "t")]
    public void A_url_carrying_a_password_gives_it_up_to_the_environment(string url, string named, string username, string password)
    {
        // A stored URL can carry its own credential; it travels the same way as a token, so it reaches no argv either.
        TokenedGitCommand.RemoteFor(url, null, null).ShouldBe(new TokenedGitCommand.Remote(named, username, password));
    }

    [Theory]
    [InlineData("https://github.com/org/repo.git")]
    [InlineData("https://user@github.com/org/repo.git")]       // a username alone is not a secret, and the operator's helper may answer for it
    [InlineData("https://user:@github.com/org/repo.git")]      // nor is an empty password
    [InlineData("ssh://git@github.com/org/repo.git")]
    [InlineData("ssh://git:pw@github.com/org/repo.git")]       // only the http transport asks a credential helper
    [InlineData("git@github.com:org/repo.git")]
    [InlineData("file:///srv/repo.git")]
    [InlineData("/srv/repo.git")]
    public void A_url_without_a_password_is_left_as_written_and_untokened(string url)
    {
        var remote = TokenedGitCommand.RemoteFor(url, null, null);
        var spec = new SandboxSpec { Command = "git", Args = new[] { "clone", url, "/tmp/x" } };

        remote.ShouldBe(new TokenedGitCommand.Remote(url, null, null));
        remote.IsTokened.ShouldBeFalse();
        TokenedGitCommand.Spec(remote, spec).ShouldBeSameAs(spec, "the operator's helpers may be how an untokened remote authenticates");
    }

    [Theory]
    [InlineData("https://ghp_pasted@host/r.git", "ghp_pasted", "")]                           // a token pasted as the user alone: sent with an empty password, as curl sent it from the URL
    [InlineData("https://ghp_pasted:x-oauth-basic@host/r.git", "ghp_pasted", "x-oauth-basic")]
    [InlineData("https://fake%2fpasted%40token@host/r.git", "fake/pasted@token", "")]
    public void A_pasted_userinfo_moves_whole_into_the_environment(string url, string username, string password)
    {
        // A pasted pack URL's bare user is a token; its caller knows that and moves the whole userinfo out of the URL.
        var remote = TokenedGitCommand.FromUserInfo(url);

        remote.ShouldBe(new TokenedGitCommand.Remote("https://host/r.git", username, password));
        remote.IsTokened.ShouldBeTrue();
    }

    [Theory]
    [InlineData("get", true)]
    [InlineData("store", false)]
    [InlineData("erase", false)]
    public async Task The_helper_answers_get_from_the_environment_and_nothing_else(string operation, bool answers)
    {
        // Run the helper as git runs a '!' helper — `sh -c '<helper> <operation>'` — with the credential in its environment.
        // Shell and printf metacharacters in the token arrive verbatim: it is an argument to printf's %s, never its format.
        if (OperatingSystem.IsWindows()) return;

        using var state = new HelperState();

        var result = await state.RunAsync(operation);

        result.Status.ShouldBe(SandboxStatus.Success, result.Stderr);
        result.Stdout.ShouldBe(answers ? $"username=x-access-token\npassword={HelperState.Password}\n" : "");
    }

    [Fact]
    public async Task The_helper_stops_answering_once_the_remote_refused_the_credential()
    {
        // git-lfs answers a 401 by telling the helpers to erase the credential and asking again, with no limit: a helper that
        // keeps answering keeps it retrying for the whole command timeout, tens of failed logins a second. git itself stops
        // after one erase. So erase records the refusal in the command's state directory, and get then answers nothing and
        // says why — git-lfs fails at once, and its message carries the reason. A store changes nothing.
        if (OperatingSystem.IsWindows()) return;

        using var state = new HelperState();

        (await state.RunAsync("store")).Stdout.ShouldBe("");
        (await state.RunAsync("get")).Stdout.ShouldNotBeEmpty("a store is not a refusal");

        (await state.RunAsync("erase")).Stdout.ShouldBe("");
        var refused = await state.RunAsync("get");

        refused.Stdout.ShouldBe("", "a refused credential is not offered again");
        refused.Stderr.ShouldContain("the remote refused this credential");
        Directory.EnumerateFileSystemEntries(state.Directory).Select(Path.GetFileName).ShouldBe(new[] { "refused" }, "the record is a marker, holding nothing");
        File.ReadAllText(Path.Combine(state.Directory, "refused")).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null)]           // a runner that did not make the directory
    [InlineData("missing")]      //   or one that is gone
    public async Task Without_its_state_directory_the_helper_answers_nothing(string? state)
    {
        // A helper that could not record a refusal could not stop git-lfs retrying one, so it never answers: the command
        // fails at once rather than loop.
        if (OperatingSystem.IsWindows()) return;

        using var helper = new HelperState();
        var directory = state is null ? null : Path.Combine(helper.Directory, state);

        var result = await helper.RunAsync("get", directory);

        result.Stdout.ShouldBe("");
    }

    [Fact]
    public void The_argv_detector_sees_a_basic_credential_for_any_user()
    {
        // Fixture check for every call-site pin: the token rides an argv as a Basic Authorization header too (an http.extraHeader),
        // base64-encoded behind its user — any user, GitHub's or GitLab's, shifting it within base64's three-byte groups.
        const string token = "fake-publish-token-0123456789";

        foreach (var user in new[] { "x-access-token", "oauth2", "u", "ab" })
        {
            var header = "http.extraHeader=Authorization: Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{user}:{token}"));

            TokenedGitSpecs.ArgvCarriesACredential(new SandboxSpec { Command = "git", Args = new[] { "-c", header, "clone", "https://host/r.git" } }, token).ShouldBeTrue(user);
        }

        TokenedGitSpecs.ArgvCarriesACredential(new SandboxSpec { Command = "git", Args = new[] { "clone", "https://host/r.git", "/tmp/fake-publish-token" } }, token).ShouldBeFalse("fixture check: a prefix of the token is not the token");
    }

    /// <summary>The helper run as git runs a '!' helper, with a fixed credential and its own state directory, removed on dispose.</summary>
    private sealed class HelperState : IDisposable
    {
        public const string Password = "p@ss/w+rd $HOME `id` \\n %s \"'";

        public string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("cs-helper-state-").FullName;

        public Task<SandboxResult> RunAsync(string operation) => RunAsync(operation, Directory);

        public Task<SandboxResult> RunAsync(string operation, string? state)
        {
            var environment = new Dictionary<string, string> { [TokenedGitCommand.UsernameVariable] = "x-access-token", [TokenedGitCommand.PasswordVariable] = Password };
            if (state is not null) environment[TokenedGitCommand.StateVariable] = state;

            return new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "/bin/sh", Args = new[] { "-c", $"{TokenedGitCommand.CredentialHelper[1..]} {operation}" }, Environment = environment, TimeoutSeconds = 15 }, CancellationToken.None);
        }

        public void Dispose()
        {
            try { System.IO.Directory.Delete(Directory, recursive: true); } catch { /* best-effort */ }
        }
    }
}
