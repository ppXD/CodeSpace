using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Harnesses.Codex;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Each repository's <c>AGENTS.md</c> run through the production pipeline of a multi-repo Codex run: does the file Codex
/// reads its instructions from, <c>$CODEX_HOME/AGENTS.md</c>, carry every repository's doc after the operating contract
/// when the run works at the workspace root, and does the run's timeline say when a doc was cut?
///
/// <para>🟡 Medium-mock (Rule 12): the real DI-wired <see cref="IAgentRunExecutor"/>, the real <see cref="CodexHarness"/>,
/// <c>LocalGitWorkspaceProvider</c> cloning each repository from a <c>file://</c> bare remote into a folder of its own
/// below the workspace root, the real <see cref="LocalProcessRunner"/> writing the run's config home, and real Postgres.
/// Only the CLI is a fake: a <c>/bin/sh</c> script armed through <see cref="CodexHarness.CommandEnvVar"/> that reads the
/// <c>AGENTS.md</c> in the <c>CODEX_HOME</c> it was really spawned with and fails the run unless the contract comes first
/// and each repository's doc after it, in order. That the real CLI hands that file to the model at a workspace root, and
/// reads no repository's doc from below it on its own, is pinned by <c>RepositoryConfigE2ETests</c>.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RealHarnessCodexMultiRepoGuidesTests
{
    private const string NoticePrefix = "Left ";

    /// <summary>The related repository's alias, and the folder below the workspace root it is cloned into.</summary>
    private const string RelatedAlias = "api";

    private readonly PostgresFixture _fixture;

    public RealHarnessCodexMultiRepoGuidesTests(PostgresFixture fixture) { _fixture = fixture; }

    [Theory]
    [InlineData(false)]   // each doc whole: nothing to say
    [InlineData(true)]    // the primary's doc runs past Codex's cap: cut where Codex cuts it, and said once
    public async Task A_multi_repo_codex_run_reads_every_repositorys_agents_md_after_the_operating_contract(bool oversized)
    {
        if (OperatingSystem.IsWindows()) return;   // the fake CLI is a /bin/sh script

        var nonce = Guid.NewGuid().ToString("N");
        var primaryDoc = $"PRIMARY-DOC-{nonce}\n" + (oversized ? new string('x', CodexRepositoryGuides.MaxProjectDocBytes) + $"\nPRIMARY-TAIL-{nonce}\n" : "");
        using var primary = new BareRemote();
        using var related = new BareRemote();
        await primary.SeedAsync(new Dictionary<string, string> { ["AGENTS.md"] = primaryDoc });
        await related.SeedAsync(new Dictionary<string, string> { ["AGENTS.override.md"] = $"OVERRIDE-DOC-{nonce}\n", ["AGENTS.md"] = $"PLAIN-DOC-{nonce}\n" });
        using var cli = new GuideCheckingFakeCli(AgentOperatingContract.Compose(null).Split('\n')[0], $"PRIMARY-DOC-{nonce}", $"OVERRIDE-DOC-{nonce}", forbidden: [$"PLAIN-DOC-{nonce}", $"PRIMARY-TAIL-{nonce}"]);

        var (teamId, userId) = await SeedTeamAsync();
        var (primaryId, relatedId) = await SeedBoundRepositoriesAsync(teamId, primary.Url, related.Url);
        var runId = await CreateRunAsync(teamId, userId, primaryId, relatedId, cli.Env());

        await ExecuteRealAsync(runId);

        var run = await LoadAsync(runId);
        var notices = (await LoadEventsAsync(runId)).Where(e => e.Text.StartsWith(NoticePrefix, StringComparison.Ordinal)).ToList();
        var read = cli.Read();

        run.Status.ShouldBe(AgentRunStatus.Succeeded, $"the fake codex fails a run whose $CODEX_HOME/AGENTS.md does not carry the contract and then each repository's doc, in order; error: {run.Error}; it read: {read}");
        read.ShouldContain($"\n\n--- project-doc ({WorkspaceSpec.DefaultAlias}/AGENTS.md) ---\n\nPRIMARY-DOC-{nonce}\n", customMessage: "the primary's doc rides under a separator naming the folder it was cloned into");
        read.ShouldContain($"\n\n--- project-doc ({RelatedAlias}/AGENTS.override.md) ---\n\nOVERRIDE-DOC-{nonce}\n", customMessage: "the related repository's override is the doc Codex itself would pick there");
        notices.Select(e => (e.Kind, e.Text)).ShouldBe(oversized ? new[] { (AgentEventKind.Warning, $"Left all but the first {CodexRepositoryGuides.MaxProjectDocBytes} bytes of the AGENTS.md of '{WorkspaceSpec.DefaultAlias}' out of this run: Codex reads no more of a project doc.") } : [], "a Warning when the run is given less of a doc than its repository holds, and nothing otherwise");
    }

    private async Task<Guid> CreateRunAsync(Guid teamId, Guid userId, Guid primaryId, Guid relatedId, IReadOnlyDictionary<string, string> env)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId);

        var workspace = new WorkspaceSpec
        {
            PrimaryAlias = WorkspaceSpec.DefaultAlias,
            Repositories = new[]
            {
                new WorkspaceRepositorySpec { Alias = WorkspaceSpec.DefaultAlias, RepositoryId = primaryId, Access = WorkspaceAccess.Write, IsPrimary = true },
                new WorkspaceRepositorySpec { Alias = RelatedAlias, RepositoryId = relatedId, Access = WorkspaceAccess.Read },
            },
        };
        var task = new AgentTask { Goal = "read every repository's instructions", Harness = CodexHarness.HarnessKind, Model = null, Workspace = workspace, Environment = env, TimeoutSeconds = 120 };
        var run = await scope.Resolve<IAgentRunService>().CreateAsync(task, teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None);

        return run.Id;
    }

    private async Task ExecuteRealAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IAgentRunExecutor>().ExecuteAsync(runId, CancellationToken.None);
    }

    private async Task<AgentRun> LoadAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
    }

    private async Task<IReadOnlyList<AgentRunEvent>> LoadEventsAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
        return await scope.Resolve<IAgentRunService>().GetEventsAsync(runId, run.TeamId, afterSequence: 0, CancellationToken.None);
    }

    private async Task<(Guid TeamId, Guid UserId)> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"guides-{userId:N}@test.local", Name = $"guides-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"guides-{teamId:N}", Name = "Codex Guides Team", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return (teamId, userId);
    }

    /// <summary>Two repositories bound through one provider instance and PAT credential, so each clone carries a token as production's does — as <c>RealHarnessWorkspaceMemoryTests</c> seeds one.</summary>
    private async Task<(Guid Primary, Guid Related)> SeedBoundRepositoriesAsync(Guid teamId, string primaryUrl, string relatedUrl)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var instanceId = Guid.NewGuid();
        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = teamId, Provider = ProviderKind.GitHub, DisplayName = "local", BaseUrl = "https://local" });

        var payloadJson = scope.Resolve<ICredentialPayloadSerializer>().Serialize(new PatPayload { Token = "agent-clone-token" });

        var credentialId = Guid.NewGuid();
        db.Credential.Add(new Credential
        {
            Id = credentialId, TeamId = teamId, ProviderInstanceId = instanceId,
            AuthType = AuthType.Pat, DisplayName = "clone cred",
            EncryptedPayload = scope.Resolve<IPayloadEncryptor>().Encrypt(payloadJson), Status = CredentialStatus.Active,
        });

        Repository Bound(string name, string cloneUrlHttps) => new()
        {
            Id = Guid.NewGuid(), TeamId = teamId, ProviderInstanceId = instanceId, CredentialId = credentialId,
            ExternalId = Guid.NewGuid().ToString(), NamespacePath = "org", Name = name, FullPath = $"org/{name}",
            DefaultBranch = "main", CloneUrlHttps = cloneUrlHttps, WebUrl = $"https://local/org/{name}",
        };

        var (primary, related) = (Bound("primary", primaryUrl), Bound("related", relatedUrl));
        db.Repository.AddRange(primary, related);

        await db.SaveChangesAsync();
        return (primary.Id, related.Id);
    }

    /// <summary>A bare local remote whose one commit holds the files given. GUID-suffixed; best-effort cleanup.</summary>
    private sealed class BareRemote : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-guides-remote-" + Guid.NewGuid().ToString("N"));
        private readonly string _bare;

        public BareRemote()
        {
            Directory.CreateDirectory(_root);
            _bare = Path.Combine(_root, "remote.git");
        }

        public string Url => new Uri(_bare).AbsoluteUri;

        public async Task SeedAsync(IReadOnlyDictionary<string, string> files)
        {
            await Git(_root, "init", "--bare", "-b", "main", _bare);

            var seed = Path.Combine(_root, "seed");
            Directory.CreateDirectory(seed);
            await Git(seed, "clone", _bare, seed);
            await Git(seed, "config", "user.email", "test@codespace.dev");
            await Git(seed, "config", "user.name", "Test");
            await Git(seed, "config", "commit.gpgsign", "false");

            foreach (var (relative, content) in files) await File.WriteAllTextAsync(Path.Combine(seed, relative), content);

            await Git(seed, "add", "-A");
            await Git(seed, "commit", "-m", "seed");
            await Git(seed, "push", "origin", "main");
        }

        private static async Task Git(string workdir, params string[] args)
        {
            var result = await new LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = args, WorkingDirectory = workdir, TimeoutSeconds = 60 }, CancellationToken.None);

            if (result.Status != SandboxStatus.Success)
                throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Stderr}");
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// The fake <c>codex</c>: copies the <c>$CODEX_HOME/AGENTS.md</c> it was spawned with aside for the test, then exits 9
    /// unless that file carries the contract's first line, then the first doc's sentinel, then the second's, each on a
    /// later line than the one before, and none of the forbidden sentinels; otherwise prints a successful Codex stream.
    /// Named and staged with the <see cref="FakeAgentCliMarker"/> markers, so a real-CLI gate elsewhere in the process
    /// sees it for a fake. Arms the process-wide <see cref="CodexHarness.CommandEnvVar"/>; restores it and deletes its
    /// directory on dispose.
    /// </summary>
    private sealed class GuideCheckingFakeCli : IDisposable
    {
        private readonly string? _original;
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "cs-guides" + FakeAgentCliMarker.DirectoryMarker + Guid.NewGuid().ToString("N"));
        private readonly string _read;
        private readonly Dictionary<string, string> _env;

        public GuideCheckingFakeCli(string contract, string first, string second, IReadOnlyList<string> forbidden)
        {
            Directory.CreateDirectory(_directory);
            _read = Path.Combine(_directory, "agents-read.md");
            _env = new Dictionary<string, string> { ["FAKE_READ"] = _read, ["FAKE_CONTRACT"] = contract, ["FAKE_FIRST"] = first, ["FAKE_SECOND"] = second, ["FAKE_FORBIDDEN"] = string.Join(' ', forbidden) };

            var script = Path.Combine(_directory, FakeAgentCliMarker.ScriptNamePrefix + "codex.sh");
            File.WriteAllText(script, """
                #!/bin/sh
                cat > /dev/null
                doc="$CODEX_HOME/AGENTS.md"
                cp "$doc" "$FAKE_READ" || { echo "no AGENTS.md in CODEX_HOME '$CODEX_HOME'" >&2; exit 9; }
                line() { grep -n -F -- "$1" "$doc" | head -1 | cut -d: -f1; }
                contract=$(line "$FAKE_CONTRACT"); first=$(line "$FAKE_FIRST"); second=$(line "$FAKE_SECOND")
                [ -n "$contract" ] && [ -n "$first" ] && [ -n "$second" ] && [ "$contract" -lt "$first" ] && [ "$first" -lt "$second" ] || { echo "AGENTS.md does not carry the contract (line '$contract'), then '$FAKE_FIRST' (line '$first'), then '$FAKE_SECOND' (line '$second')" >&2; exit 9; }
                for word in $FAKE_FORBIDDEN; do
                  ! grep -q -F -- "$word" "$doc" || { echo "AGENTS.md carries '$word', which the run must not be given" >&2; exit 9; }
                done
                printf '%s\n' '{"type":"agent_message","message":"read every guide"}' '{"type":"task_complete","message":"completed"}'

                """);
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            _original = Environment.GetEnvironmentVariable(CodexHarness.CommandEnvVar);
            Environment.SetEnvironmentVariable(CodexHarness.CommandEnvVar, script);
        }

        public IReadOnlyDictionary<string, string> Env() => _env;

        /// <summary>The <c>AGENTS.md</c> the fake found in its <c>CODEX_HOME</c>; empty when it never ran.</summary>
        public string Read() => File.Exists(_read) ? File.ReadAllText(_read) : "";

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(CodexHarness.CommandEnvVar, _original);
            try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort */ }
        }
    }
}
