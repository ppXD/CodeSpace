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
/// import must get a checkout that holds no token once cloned, in a directory no other uid can read; a failed import must name
/// no token in its message — the text that reaches the API error body, the UI and the mediator's error log — even where git
/// itself echoes it (a token pasted as the user alone, in any spelling); and that user-alone token must reach neither the
/// operator's credential helpers nor their trace2 targets.
///
/// <para>Positive controls: the remote refuses a clone without the token; the same production clone with origin left as git
/// wrote it holds the token, so the scan that finds none can see one; git's raw stderr carried the token wherever the message
/// is clean of an echo, so the clean message is the redaction's doing; and the clone run without the helper reset and with
/// trace2 on hands the token to the operator's helper and trace2 targets, so their silence is the tokened clone's doing. Each
/// test owns its remote and a scratch HOME (a global config of its own, system config off), and removes both on every path;
/// nothing reads or writes the real global config or keychain.</para>
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
    [InlineData(true)]   // positive control: origin left as git wrote it
    public async Task A_pasted_token_clones_and_the_checkout_holds_no_token(bool originAsCloned)
    {
        if (OperatingSystem.IsWindows() || !await GitAvailableAsync()) return;

        await using var ctx = await ScratchHostContext.StartAsync();
        ctx.Runner.KeepOriginAsCloned = originAsCloned;

        await Should.ThrowAsync<PackImportException>(() => ctx.Fetcher.FetchAsync(ctx.Remote.Url, null, CancellationToken.None), "fixture check: the remote refuses a clone that presents no token");

        using var checkout = await ctx.Fetcher.FetchAsync(ctx.UrlWith($"x-access-token:{GitPublishRemoteFixture.FakeToken}"), null, CancellationToken.None);

        File.ReadAllText(Path.Combine(checkout.Directory, "README.md")).ShouldBe("base, revised\n", "the clone authenticated with the pasted token");
        ctx.Runner.Ran("remote").ShouldBeTrue("fixture check: the fetcher asked for origin to be rewritten");
        FilesHolding(checkout.Directory, GitPublishRemoteFixture.FakeToken).ShouldBe(originAsCloned ? new[] { ".git/config" } : Array.Empty<string>(), originAsCloned ? "positive control: git writes the pasted URL into origin" : "the checkout the import walks holds the pasted token");

        if (!originAsCloned) (await ctx.OriginUrlAsync(checkout.Directory)).ShouldBe(ctx.Remote.Url, "origin was rewritten to the remote without its token, not removed");

        File.GetUnixFileMode(checkout.Directory).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, "git cloned into the owner-only directory without widening it, so no other uid read .git/config while it held the token");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]   // positive control: the clone run without the helper reset and with trace2 on
    public async Task A_token_pasted_as_the_user_alone_reaches_no_credential_helper_or_trace2_target(bool unreset)
    {
        // git asks the credential helpers for the password the URL lacks, naming the user, and writes the clone's argv and
        // its remote-http child's to the trace2 targets — both from the operator's global config.
        if (OperatingSystem.IsWindows() || !await GitAvailableAsync()) return;

        await using var ctx = await ScratchHostContext.StartAsync();
        ctx.ConfigureLoggingHelperAndTrace2();

        await Should.ThrowAsync<PackImportException>(() => ctx.Fetcher.FetchAsync(ctx.Remote.Url, null, CancellationToken.None), "fixture check: the remote refuses a clone that presents no token");
        ctx.HelperLog().ShouldContain("op=get", Case.Sensitive, "fixture check: an untokened clone of the remote asks the operator's helper");
        ctx.OperatorTrace().ShouldContain(ctx.Remote.Url, Case.Sensitive, "fixture check: the operator's trace2 targets record an untokened clone");

        ctx.Runner.Unreset = unreset;
        await Should.ThrowAsync<PackImportException>(() => ctx.Fetcher.FetchAsync(ctx.UrlWith("fake-pasted-token-0123456789"), null, CancellationToken.None), "git asks for a password the pasted URL does not carry");

        ctx.HelperLog().Contains(Marker, StringComparison.Ordinal).ShouldBe(unreset, unreset ? "positive control: without the reset git hands the pasted user to the operator's helper" : "the pasted token reached the operator's credential helper");
        ctx.OperatorTrace().Contains(Marker, StringComparison.Ordinal).ShouldBe(unreset, unreset ? "positive control: with trace2 on git records the pasted URL" : "the pasted token reached the operator's trace2 targets");
    }

    [Theory]
    [InlineData("x-access-token:fake-wrong-token-0123456789", false)]   // a wrong or revoked token: git hides the password, the message used to name it in the URL
    [InlineData("fake-pasted-token-0123456789", true)]                  // a token pasted as the user: git asks for a password and names the user
    [InlineData("fake%2fpasted%40token-0123456789", true)]              // the same, percent-encoded: git echoes it decoded or re-encoded
    public async Task A_failed_clone_names_no_pasted_token(string userInfo, bool gitEchoesIt)
    {
        if (OperatingSystem.IsWindows() || !await GitAvailableAsync()) return;

        await using var ctx = await ScratchHostContext.StartAsync();

        var failure = await Should.ThrowAsync<PackImportException>(() => ctx.Fetcher.FetchAsync(ctx.UrlWith(userInfo), null, CancellationToken.None));

        ctx.Runner.Stderr.Contains(Marker, StringComparison.Ordinal).ShouldBe(gitEchoesIt, "fixture check: where git echoed the pasted token");
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

        ctx.Runner.Stderr.ShouldContain("fake-pasted-token-0123456789", Case.Sensitive, "fixture check: git echoed the pasted token");
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
        /// what git sends: protocol, host, username) and answers none, and all three trace2 targets pointed at scratch files.
        /// </summary>
        public void ConfigureLoggingHelperAndTrace2()
        {
            var helper = Path.Combine(_home, "logging-helper.sh");
            File.WriteAllText(helper, $"#!/bin/sh\necho \"op=$1\" >> '{HelperLogFile}'\ncat >> '{HelperLogFile}'\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            File.WriteAllText(GlobalConfig, $"[credential]\n\thelper = {helper}\n[trace2]\n\tnormalTarget = {TraceFile("normal")}\n\teventTarget = {TraceFile("event")}\n\tperfTarget = {TraceFile("perf")}\n");
        }

        /// <summary>Every request git made of the operator's helper.</summary>
        public string HelperLog() => File.Exists(HelperLogFile) ? File.ReadAllText(HelperLogFile) : "";

        /// <summary>Everything the operator's trace2 targets recorded.</summary>
        public string OperatorTrace() => string.Concat(new[] { "normal", "event", "perf" }.Select(TraceFile).Where(File.Exists).Select(File.ReadAllText));

        /// <summary>The remote's URL as an operator would paste it with <paramref name="userInfo"/> in it.</summary>
        public string UrlWith(string userInfo) => Remote.Url.Replace("http://", $"http://{userInfo}@", StringComparison.Ordinal);

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
    /// The real local runner on the scratch host, recording each subcommand and git's raw stderr. With
    /// <see cref="KeepOriginAsCloned"/> set, the <c>git remote</c> edits that strip a pasted credential report success without
    /// running, so origin stays as git wrote it; with <see cref="Unreset"/> set, the clone runs without the credential-helper
    /// reset and with trace2 on — the positive controls.
    /// </summary>
    private sealed class ScratchHostRunner(IReadOnlyDictionary<string, string> environment) : ISandboxRunner
    {
        private static readonly string[] TraceOff = { "GIT_TRACE2", "GIT_TRACE2_EVENT", "GIT_TRACE2_PERF" };

        private readonly LocalProcessRunner _inner = new();
        private readonly List<SandboxSpec> _specs = new();

        public string Kind => "local";
        public IReadOnlyDictionary<string, string> Environment => environment;
        public bool KeepOriginAsCloned { get; set; }
        public bool Unreset { get; set; }
        public string Stderr { get; private set; } = "";

        public bool Ran(string subcommand) => _specs.Any(s => s.Args.Contains(subcommand));

        public async Task<SandboxResult> RunAsync(SandboxSpec spec, CancellationToken cancellationToken)
        {
            _specs.Add(spec);

            if (KeepOriginAsCloned && spec.Args.Contains("remote")) return new SandboxResult { Status = SandboxStatus.Success, ExitCode = 0, Stdout = "", Stderr = "" };

            var unreset = Unreset && spec.Args.Contains("clone");
            var env = new Dictionary<string, string>(spec.Environment);
            foreach (var (key, value) in environment) env[key] = value;
            if (unreset) foreach (var key in TraceOff) env.Remove(key);

            var result = await _inner.RunAsync(spec with { Args = unreset ? WithoutTheReset(spec.Args) : spec.Args, Environment = env }, cancellationToken);
            Stderr += result.Stderr;
            return result;
        }

        private static IReadOnlyList<string> WithoutTheReset(IReadOnlyList<string> args)
        {
            var at = Enumerable.Range(0, Math.Max(0, args.Count - 1)).FirstOrDefault(i => args[i] == "-c" && args[i + 1].StartsWith("credential.", StringComparison.Ordinal) && args[i + 1].EndsWith(".helper=", StringComparison.Ordinal), -1);

            return at < 0 ? args : args.Take(at).Concat(args.Skip(at + 2)).ToList();
        }
    }

    private sealed class AllowAll : IPackHostAllowlist
    {
        public bool IsAllowed(string url) => true;
        public void EnsureAllowed(string url) { }
    }
}
