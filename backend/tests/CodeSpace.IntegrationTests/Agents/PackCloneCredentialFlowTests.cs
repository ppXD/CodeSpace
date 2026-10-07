using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Identity;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// HIGH fidelity: the REAL <see cref="PackCloneFetcher"/> on the real <see cref="LocalProcessRunner"/> and real <c>git</c>,
/// against a loopback smart-HTTP remote (<see cref="GitPublishRemoteFixture"/>) that demands a FAKE token for every read, so a
/// clone that succeeds proves the pasted token authenticated it. An operator who pastes a URL carrying a token into the pack
/// import must get a checkout that never holds the token — the clone names the remote without it, so git writes none even
/// before origin is rewritten — in a directory no other uid can read, with no argv carrying it; a failed import must name no
/// token in its message — the text that reaches the API error body, the UI and the mediator's error log — in any spelling;
/// and a token pasted as the user alone must reach neither the operator's credential helpers nor their trace2 targets.
///
/// <para>Positive controls: the remote refuses a clone without the token; a raw clone of the pasted URL leaves the token in
/// .git/config, so the scan that finds none can see one; and the clone run without the helper reset and with trace2 on hands
/// the token to the operator's helper (told to erase it once refused) and trace2 targets (recording the credential
/// variables), so their silence is the tokened clone's doing. Each test owns its remote and a scratch HOME (a global config
/// of its own, system config off), and removes both on every path; nothing reads or writes the real global config or
/// keychain.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PackCloneCredentialFlowTests
{
    /// <summary>In every fake credential below, in every spelling git echoes it in, so one absence check covers them all.</summary>
    private const string Marker = "token-0123456789";

    private readonly PostgresFixture _fixture;

    public PackCloneCredentialFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]   // origin left as git wrote it: the strip is a belt, the clone never wrote the token
    public async Task A_pasted_token_clones_and_the_checkout_never_holds_it(bool originAsCloned)
    {
        if (OperatingSystem.IsWindows() || !await GitAvailableAsync()) return;

        await using var ctx = await ScratchHostContext.StartAsync();
        ctx.Runner.KeepOriginAsCloned = originAsCloned;

        await Should.ThrowAsync<PackImportException>(() => ctx.Fetcher.FetchAsync(ctx.Remote.Url, null, CancellationToken.None), "fixture check: the remote refuses a clone that presents no token");

        using var checkout = await ctx.Fetcher.FetchAsync(ctx.UrlWith($"x-access-token:{GitPublishRemoteFixture.FakeToken}"), null, CancellationToken.None);

        File.ReadAllText(Path.Combine(checkout.Directory, "README.md")).ShouldBe("base, revised\n", "the clone authenticated with the pasted token");
        ctx.Runner.Ran("remote").ShouldBeTrue("fixture check: the fetcher asked for origin to be rewritten");
        FilesHolding(checkout.Directory, GitPublishRemoteFixture.FakeToken).ShouldBeEmpty("the checkout the import walks holds the pasted token");
        (await ctx.OriginUrlAsync(checkout.Directory)).ShouldBe(ctx.Remote.Url, "origin names the remote without its token, whether or not the strip ran");
        ctx.Runner.Argvs.Where(a => a.Contains(Marker, StringComparison.Ordinal) || a.Contains("@127.0.0.1", StringComparison.Ordinal)).ShouldBeEmpty("no argv carries the pasted credential");

        File.GetUnixFileMode(checkout.Directory).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, "git cloned into the owner-only directory without widening it");

        var raw = await ctx.RawCloneAsync(ctx.UrlWith($"x-access-token:{GitPublishRemoteFixture.FakeToken}"));
        FilesHolding(raw, GitPublishRemoteFixture.FakeToken).ShouldBe(new[] { ".git/config" }, "positive control: git writes a URL it clones, credential and all, into origin");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]   // positive control: the clone run without the helper reset and with trace2 on
    public async Task A_token_pasted_as_the_user_alone_reaches_no_credential_helper_or_trace2_target(bool unreset)
    {
        // git tells the credential helpers to erase a credential the remote refused, naming its user, and writes the values of
        // the variables trace2.envVars names to the trace2 targets — both from the operator's global config.
        if (OperatingSystem.IsWindows() || !await GitAvailableAsync()) return;

        await using var ctx = await ScratchHostContext.StartAsync();
        ctx.ConfigureLoggingHelperAndTrace2();

        await Should.ThrowAsync<PackImportException>(() => ctx.Fetcher.FetchAsync(ctx.Remote.Url, null, CancellationToken.None), "fixture check: the remote refuses a clone that presents no token");
        ctx.HelperLog().ShouldContain("op=get", Case.Sensitive, "fixture check: an untokened clone of the remote asks the operator's helper");
        ctx.OperatorTrace().ShouldContain(ctx.Remote.Url, Case.Sensitive, "fixture check: the operator's trace2 targets record an untokened clone");

        ctx.Runner.Unreset = unreset;
        await Should.ThrowAsync<PackImportException>(() => ctx.Fetcher.FetchAsync(ctx.UrlWith("fake-pasted-token-0123456789"), null, CancellationToken.None), "the remote refuses the pasted user with an empty password");

        ctx.HelperLog().Contains(Marker, StringComparison.Ordinal).ShouldBe(unreset, unreset ? "positive control: without the reset git tells the operator's helper to erase the refused pasted user" : "the pasted token reached the operator's credential helper");
        ctx.OperatorTrace().Contains(Marker, StringComparison.Ordinal).ShouldBe(unreset, unreset ? "positive control: with trace2 on git records the credential variables" : "the pasted token reached the operator's trace2 targets");
    }

    [Theory]
    [InlineData("x-access-token:fake-wrong-token-0123456789")]   // a wrong or revoked token
    [InlineData("fake-pasted-token-0123456789")]                  // a token pasted as the user
    [InlineData("fake%2fpasted%40token-0123456789")]              // the same, percent-encoded
    public async Task A_failed_clone_names_no_pasted_token(string userInfo)
    {
        // git names the remote only by the URL it was handed, which carries no userinfo, so it echoes the token nowhere; the
        // message's own redaction stays as a belt for a remote that echoes it.
        if (OperatingSystem.IsWindows() || !await GitAvailableAsync()) return;

        await using var ctx = await ScratchHostContext.StartAsync();

        var failure = await Should.ThrowAsync<PackImportException>(() => ctx.Fetcher.FetchAsync(ctx.UrlWith(userInfo), null, CancellationToken.None));

        ctx.Runner.Stderr.ShouldContain("Authentication failed", Case.Sensitive, "fixture check: the remote refused the pasted credential");
        ctx.Runner.Stderr.ShouldNotContain(Marker, Case.Sensitive, "git echoed the pasted token");
        failure.Message.ShouldNotContain(Marker, Case.Sensitive, "the message reaches the API error body, the UI and the mediator's error log");
        failure.Message.ShouldContain($"'{ctx.Remote.Url}'", Case.Sensitive, "the message still names the remote, without its userinfo");
        failure.Message.ShouldContain("exit 128");
    }

    [Fact]
    public async Task A_failed_import_through_the_mediator_names_no_pasted_token()
    {
        if (OperatingSystem.IsWindows() || !await GitAvailableAsync()) return;

        await using var ctx = await ScratchHostContext.StartAsync();
        var (teamId, userId) = await SeedTeamAsync();

        using var scope = _fixture.BeginScope(b =>
        {
            b.RegisterInstance(new TestCurrentUser(userId, "test", Roles.Admin)).As<ICurrentUser>().SingleInstance();
            b.RegisterInstance(new TestCurrentTeam(teamId)).As<ICurrentTeam>().SingleInstance();
            b.RegisterInstance(ctx.Fetcher).As<IPackSourceFetcher>();
        });

        var import = new ImportPackFromUrlCommand { Url = ctx.UrlWith("fake-pasted-token-0123456789"), SourcePaths = new[] { "agents/reviewer.md" } };

        var failure = await Should.ThrowAsync<PackImportException>(() => scope.Resolve<IMediator>().Send(import));

        ctx.Runner.Stderr.ShouldContain("Authentication failed", Case.Sensitive, "fixture check: the clone presented the pasted credential and the remote refused it");
        failure.Message.ShouldNotContain(Marker, Case.Sensitive, "what the import surfaces to the API error body and the mediator's error log");
    }

    /// <summary>Every file under <paramref name="directory"/> whose bytes contain <paramref name="text"/>, relative and with forward slashes — the whole checkout, not just <c>.git/config</c>.</summary>
    private static string[] FilesHolding(string directory, string text) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file).Contains(text, StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(directory, file).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static async Task<bool> GitAvailableAsync()
    {
        try { return (await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = new[] { "--version" }, TimeoutSeconds = 15 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    private async Task<(Guid TeamId, Guid UserId)> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"packcred-{userId:N}@test.local", Name = $"packcred-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"packcred-{teamId:N}", Name = "Pack Credential Team", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return (teamId, userId);
    }

    /// <summary>The remote, a scratch host (HOME, a global config absent until a test writes one, system config off) and the production fetcher running on it.</summary>
    private sealed class ScratchHostContext : IAsyncDisposable
    {
        private readonly string _home = Directory.CreateTempSubdirectory("cs-packcred-home-").FullName;

        private ScratchHostContext()
        {
            Runner = new ScratchHostRunner(new Dictionary<string, string> { ["HOME"] = _home, ["GIT_CONFIG_GLOBAL"] = GlobalConfig, ["GIT_CONFIG_NOSYSTEM"] = "1" });
            Fetcher = new PackCloneFetcher(new AllowAll(), new SandboxRunnerRegistry(new ISandboxRunner[] { Runner }), NullLogger<PackCloneFetcher>.Instance);
        }

        public GitPublishRemoteFixture Remote { get; } = new() { AuthenticateReads = true };
        public ScratchHostRunner Runner { get; }
        public PackCloneFetcher Fetcher { get; }

        public static async Task<ScratchHostContext> StartAsync()
        {
            var ctx = new ScratchHostContext();

            try
            {
                await ctx.Remote.StartAsync();
                return ctx;
            }
            catch
            {
                await ctx.DisposeAsync();
                throw;
            }
        }

        private string GlobalConfig => Path.Combine(_home, "global-config");
        private string HelperLogFile => Path.Combine(_home, "helper.log");
        private string TraceFile(string target) => Path.Combine(_home, "trace2-" + target);

        /// <summary>
        /// The operator's global config: a credential helper that logs every request git makes of it (the operation, then
        /// what git sends: protocol, host, username) and answers none, and all three trace2 targets pointed at scratch files,
        /// recording the credential variables too.
        /// </summary>
        public void ConfigureLoggingHelperAndTrace2()
        {
            var helper = Path.Combine(_home, "logging-helper.sh");
            File.WriteAllText(helper, $"#!/bin/sh\necho \"op=$1\" >> '{HelperLogFile}'\ncat >> '{HelperLogFile}'\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            File.WriteAllText(GlobalConfig, $"[credential]\n\thelper = {helper}\n[trace2]\n\tnormalTarget = {TraceFile("normal")}\n\teventTarget = {TraceFile("event")}\n\tperfTarget = {TraceFile("perf")}\n\tenvVars = CODESPACE_GIT_USERNAME,CODESPACE_GIT_PASSWORD\n");
        }

        /// <summary>Every request git made of the operator's helper.</summary>
        public string HelperLog() => File.Exists(HelperLogFile) ? File.ReadAllText(HelperLogFile) : "";

        /// <summary>Everything the operator's trace2 targets recorded.</summary>
        public string OperatorTrace() => string.Concat(new[] { "normal", "event", "perf" }.Select(TraceFile).Where(File.Exists).Select(File.ReadAllText));

        /// <summary>The remote's URL as an operator would paste it with <paramref name="userInfo"/> in it.</summary>
        public string UrlWith(string userInfo) => Remote.Url.Replace("http://", $"http://{userInfo}@", StringComparison.Ordinal);

        /// <summary>A raw clone of <paramref name="url"/> on the scratch host, as git is handed it, into a directory of its own; never recorded by the runner, never production code.</summary>
        public async Task<string> RawCloneAsync(string url)
        {
            var directory = Path.Combine(_home, "raw-clone");
            var result = await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = new[] { "-c", "core.hooksPath=/dev/null", "clone", url, directory }, Environment = Runner.Environment, TimeoutSeconds = 60, AllowNetwork = true }, CancellationToken.None);

            result.Status.ShouldBe(SandboxStatus.Success, $"fixture check: the raw clone failed: {result.Stderr}");
            return directory;
        }

        /// <summary>The clone's origin URL as git reads it, on the scratch host; never recorded by the runner.</summary>
        public async Task<string> OriginUrlAsync(string cloneDir)
        {
            var result = await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = new[] { "-C", cloneDir, "config", "--get", "remote.origin.url" }, Environment = Runner.Environment, TimeoutSeconds = 30 }, CancellationToken.None);

            result.Status.ShouldBe(SandboxStatus.Success, $"git config --get remote.origin.url failed: {result.Stderr}");
            return result.Stdout.Trim();
        }

        public async ValueTask DisposeAsync()
        {
            await Remote.DisposeAsync();
            try { Directory.Delete(_home, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// The real local runner on the scratch host, recording each argv and git's raw stderr. With
    /// <see cref="KeepOriginAsCloned"/> set, the <c>git remote</c> edits that strip a pasted credential report success without
    /// running, so origin stays as git wrote it; with <see cref="Unreset"/> set, the clone runs without the credential-helper
    /// reset and with trace2 on — the positive control.
    /// </summary>
    private sealed class ScratchHostRunner(IReadOnlyDictionary<string, string> environment) : ISandboxRunner
    {
        private readonly LocalProcessRunner _inner = new();
        private readonly List<SandboxSpec> _specs = new();

        public string Kind => "local";
        public IReadOnlyDictionary<string, string> Environment => environment;
        public bool KeepOriginAsCloned { get; set; }
        public bool Unreset { get; set; }
        public string Stderr { get; private set; } = "";

        /// <summary>Every argv the fetcher handed the runner, joined.</summary>
        public IEnumerable<string> Argvs => _specs.Select(s => string.Join(' ', s.Args));

        public bool Ran(string subcommand) => _specs.Any(s => s.Args.Contains(subcommand));

        public async Task<SandboxResult> RunAsync(SandboxSpec spec, CancellationToken cancellationToken)
        {
            _specs.Add(spec);

            if (KeepOriginAsCloned && spec.Args.Contains("remote")) return new SandboxResult { Status = SandboxStatus.Success, ExitCode = 0, Stdout = "", Stderr = "" };

            var env = Unreset && spec.Args.Contains("clone") ? TokenedGitControls.WithoutTheReset(spec.Environment) : new Dictionary<string, string>(spec.Environment);
            foreach (var (key, value) in environment) env[key] = value;

            var result = await _inner.RunAsync(spec with { Environment = env }, cancellationToken);
            Stderr += result.Stderr;
            return result;
        }
    }

    private sealed class AllowAll : IPackHostAllowlist
    {
        public bool IsAllowed(string url) => true;
        public void EnsureAllowed(string url) { }
    }
}
