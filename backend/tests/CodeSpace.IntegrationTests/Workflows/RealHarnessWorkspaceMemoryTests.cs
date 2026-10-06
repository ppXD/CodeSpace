using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Harnesses.Claude;
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
/// A repository's memory run through the production pipeline: does the CLI get its workspace added back for memory, and
/// does the run's timeline say why not when it doesn't — for a <c>CLAUDE.md</c> committed as a symlink — does a
/// nested directory holding memory ride the same <c>--add-dir</c>, committed or written by an earlier round, and does
/// each round's config home point at the scoped rules and the over-budget directories its workspace holds?
///
/// <para>🟡 Medium-mock (Rule 12): the real DI-wired <see cref="IAgentRunExecutor"/>, the real
/// <see cref="ClaudeCodeHarness"/>, <c>LocalGitWorkspaceProvider</c> cloning a <c>file://</c> bare remote (the symlink
/// arrives the way git checks one out), the real <see cref="LocalProcessRunner"/>, the real push, grader and revise loop,
/// and real Postgres. Only the CLI is a fake: a <c>/bin/sh</c> script armed through
/// <see cref="ClaudeCodeHarness.CommandEnvVar"/> that reads the argv it was really spawned with and fails the round
/// unless its <c>--add-dir</c> names exactly the directories the round must load memory from. The real CLI following
/// the link, and loading a nested directory's memory in place, is pinned by <c>RepositoryConfigE2ETests</c>.</para>
///
/// <para>Every run takes a revise round — the first round drafts work its check refuses — because the executor builds a
/// fresh spec for each round: the notice must be said once while the workspace stays as it was, and said again when the
/// draft round's agent re-points <c>CLAUDE.md</c>, in either direction; a nested <c>CLAUDE.md</c> the draft round writes
/// must be added for the revision.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RealHarnessWorkspaceMemoryTests
{
    /// <summary>The check the contract runs on the produced branch: PASS iff the revision has run.</summary>
    private const string CheckScript = "#!/bin/sh\ngrep -q revised feature.txt\n";

    private const string NoticePrefix = "Left ";

    private const string LeftOutNotice = "Left the memory in the workspace out of this run: CLAUDE.md resolves outside the workspace.";

    /// <summary>What <c>CLAUDE.md</c> is committed as, or what the draft round re-points it to: a file outside the workspace, or the <c>AGENTS.md</c> beside it.</summary>
    private const string Outside = "outside";

    private readonly PostgresFixture _fixture;

    public RealHarnessWorkspaceMemoryTests(PostgresFixture fixture) { _fixture = fixture; }

    [Theory]
    [InlineData(Outside, null, new[] { LeftOutNotice })]                                                     // linked outside throughout: left out of both rounds, said once
    [InlineData("AGENTS.md", null, new string[0])]                                                            // the control: linked inside throughout, nothing to say
    [InlineData("AGENTS.md", Outside, new[] { LeftOutNotice })]                                              // the draft re-points it outside: said when the revision leaves it out
    [InlineData(Outside, "AGENTS.md", new[] { LeftOutNotice, AgentRunExecutor.LaunchNoticesClearedNote })]   // the draft re-points it inside: said when the revision loads it again
    public async Task A_repository_memory_that_links_outside_is_left_out_of_each_round_it_links_out_and_said_when_that_changes(string committed, string? draftRepoints, string[] said)
    {
        if (OperatingSystem.IsWindows()) return;   // the fake CLI is a /bin/sh script, the link a POSIX symlink

        using var outside = new OutsideFile();
        using var remote = new BareRemote();
        await remote.SeedAsync(CheckScript, claudeMdTarget: Target(committed, outside));
        using var cli = new MemoryCheckingFakeCli(draft: Expected(committed), revision: Expected(draftRepoints ?? committed), draftAction: draftRepoints is null ? null : $"ln -sfn '{Target(draftRepoints, outside)}' CLAUDE.md");

        var (teamId, userId) = await SeedTeamAsync();
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url);
        var runId = await CreateRunAsync(teamId, userId, repoId, cli.Env());

        await ExecuteRealAsync(runId);

        var (run, result) = await LoadAsync(runId);
        var notices = (await LoadEventsAsync(runId)).Where(e => e.Text.StartsWith(NoticePrefix, StringComparison.Ordinal)).ToList();

        run.Status.ShouldBe(AgentRunStatus.Succeeded, $"the fake CLI fails a round whose argv adds a workspace whose CLAUDE.md links outside, or leaves out one whose CLAUDE.md links inside; error: {run.Error}; launches: {string.Join(", ", cli.Launches())}");
        cli.Launches().ShouldBe(new[] { Expected(committed), Expected(draftRepoints ?? committed) }, "fixture check: both rounds spawned the CLI, each with the argv it was meant to have");
        result.ReviseRounds.ShouldBe(1, "fixture check: the drafted round failed its check, so the executor built a second spec for the revision");
        notices.Select(e => (e.Kind, e.Text)).ShouldBe(said.Select(text => (AgentEventKind.Warning, text)), "a Warning when the run first leaves memory out, and another only when a round's agent changed what is left out");
    }

    [Theory]
    [InlineData(true, false, ". pkg", ". pkg")]   // a committed pkg/CLAUDE.md is added in place for both rounds
    [InlineData(false, true, ".", ". lib")]       // the draft round writes lib/CLAUDE.md: the revision is launched with it added
    public async Task A_nested_claude_md_is_added_in_place_and_one_a_round_writes_is_added_for_the_next(bool committed, bool draftWrites, string draft, string revision)
    {
        if (OperatingSystem.IsWindows()) return;   // the fake CLI is a /bin/sh script

        using var remote = new BareRemote();
        await remote.SeedAsync(CheckScript, claudeMdTarget: "AGENTS.md", files: committed ? new Dictionary<string, string> { ["pkg/CLAUDE.md"] = "Keep the package's API stable.\n" } : null);
        using var cli = new MemoryCheckingFakeCli(draft, revision, draftAction: draftWrites ? "mkdir -p lib && printf 'Keep the library pure.\\n' > lib/CLAUDE.md" : null);

        var (teamId, userId) = await SeedTeamAsync();
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url);
        var runId = await CreateRunAsync(teamId, userId, repoId, cli.Env());

        await ExecuteRealAsync(runId);

        var (run, result) = await LoadAsync(runId);
        var notices = (await LoadEventsAsync(runId)).Where(e => e.Text.StartsWith(NoticePrefix, StringComparison.Ordinal)).ToList();

        run.Status.ShouldBe(AgentRunStatus.Succeeded, $"the fake CLI fails a round whose --add-dir does not name exactly the workspace and its nested memory; error: {run.Error}; launches: {string.Join(", ", cli.Launches())}");
        cli.Launches().ShouldBe(new[] { draft, revision }, "each round is launched with the nested memory its workspace holds when the round starts");
        result.ReviseRounds.ShouldBe(1, "fixture check: the drafted round failed its check, so the executor built a second spec for the revision");
        notices.ShouldBeEmpty("nothing was left out");
    }

    [Fact]
    public async Task A_large_tree_and_its_scoped_rules_reach_each_round_as_pointer_rules_its_own_config_home_carries()
    {
        // Past the in-place budget no nested directory is added; each is pointed at from the round's config home, beside
        // a pointer for the committed scoped rule, whose glob the fake reads back rebased onto the cwd — but not pkg-out,
        // whose CLAUDE.md links outside the workspace: the guard leaves it without one, and says so once. The draft
        // round's agent writes a scoped rule of its own: the revision's fresh config home points at it, the draft's did not.
        if (OperatingSystem.IsWindows()) return;   // the fake CLI is a /bin/sh script

        var packages = Enumerable.Range(0, ClaudeWorkspaceMemory.MaxInPlaceDirectories + 1).Select(i => $"pkg-{i:00}").ToList();
        var files = packages.ToDictionary(package => $"{package}/CLAUDE.md", _ => "Keep the package's API stable.\n");
        var directories = string.Join(' ', packages.Select(package => package == "pkg-00" ? "/pkg-00 /pkg-00/**/*.ts" : $"/{package}"));

        files["pkg-00/.claude/rules/ts.md"] = "---\npaths: \"*.ts\"\n---\nType every export.\n";

        using var outside = new OutsideFile();
        using var remote = new BareRemote();
        await remote.SeedAsync(CheckScript, claudeMdTarget: "AGENTS.md", files: files, links: new Dictionary<string, string> { ["pkg-out/CLAUDE.md"] = outside.Path });
        using var cli = new MemoryCheckingFakeCli($". ; {directories}", $". ; /lib/py/*.py {directories}", draftAction: "mkdir -p lib/.claude/rules && printf -- '---\\npaths: py/*.py\\n---\\nPython.\\n' > lib/.claude/rules/py.md", pointers: true);

        var (teamId, userId) = await SeedTeamAsync();
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url);
        var runId = await CreateRunAsync(teamId, userId, repoId, cli.Env());

        await ExecuteRealAsync(runId);

        var (run, result) = await LoadAsync(runId);
        var notices = (await LoadEventsAsync(runId)).Where(e => e.Text.StartsWith(NoticePrefix, StringComparison.Ordinal)).ToList();

        run.Status.ShouldBe(AgentRunStatus.Succeeded, $"the fake CLI fails a round whose argv or config-home pointers are not what its workspace holds when it starts; error: {run.Error}; launches: {string.Join(", ", cli.Launches())}");
        cli.Launches().ShouldBe(new[] { $". ; {directories}", $". ; /lib/py/*.py {directories}" }, "each round's own config home points at what its workspace holds when the round starts");
        result.ReviseRounds.ShouldBe(1, "fixture check: the drafted round failed its check, so the executor built a second spec for the revision");
        notices.Select(e => e.Text).ShouldBe(new[] { $"Left the memory of every nested directory out of the run's first request: together it spans more than {ClaudeWorkspaceMemory.MaxInPlaceDirectories} directories or {ClaudeWorkspaceMemory.MaxInPlaceBytes} bytes. A read below one of them points the run at that directory's memory instead. Left the memory in 'pkg-out' out of this run: CLAUDE.md resolves outside the workspace." }, "said once, in one event: the revision leaves out what the draft did");
    }

    /// <summary>What <c>CLAUDE.md</c> links to for <paramref name="where"/>.</summary>
    private static string Target(string where, OutsideFile outside) => where == Outside ? outside.Path : where;

    /// <summary>What a launch's argv must say about the workspace when <c>CLAUDE.md</c> links to <paramref name="where"/>: no <c>--add-dir</c>, or the workspace alone.</summary>
    private static string Expected(string where) => where == Outside ? "absent" : ".";

    private static AgentTask TaskWith(Guid repositoryId, IReadOnlyDictionary<string, string> env) => new()
    {
        Goal = "make feature.txt say the right thing",
        Harness = ClaudeCodeHarness.HarnessKind,
        Model = null,
        RepositoryId = repositoryId,
        Environment = env,
        TimeoutSeconds = 120,
        Acceptance = new SupervisorAcceptanceSpec { Command = new[] { "sh", "check.sh" }, Description = "the file check" },
        // The contract-implies-gradable-branch invariant AgentCodeNode bakes at authoring, mirrored because the task is built here.
        PushProducedBranch = true,
        MaxReviseRounds = 1,
    };

    private async Task<Guid> CreateRunAsync(Guid teamId, Guid userId, Guid repositoryId, IReadOnlyDictionary<string, string> env)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId);
        var run = await scope.Resolve<IAgentRunService>().CreateAsync(TaskWith(repositoryId, env), teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None);
        return run.Id;
    }

    private async Task ExecuteRealAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IAgentRunExecutor>().ExecuteAsync(runId, CancellationToken.None);
    }

    private async Task<(AgentRun Run, AgentRunResult Result)> LoadAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
        return (run, JsonSerializer.Deserialize<AgentRunResult>(run.ResultJson ?? "{}", AgentJson.Options)!);
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
        db.User.Add(new User { Id = userId, Email = $"memory-{userId:N}@test.local", Name = $"memory-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"memory-{teamId:N}", Name = "Workspace Memory Team", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return (teamId, userId);
    }

    /// <summary>A bound repository with a PAT credential, so the clone carries a token and the push path activates — as <c>AgentRunReviseLoopFlowTests</c> seeds it.</summary>
    private async Task<Guid> SeedBoundRepositoryAsync(Guid teamId, string cloneUrlHttps)
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

        var repoId = Guid.NewGuid();
        db.Repository.Add(new Repository
        {
            Id = repoId, TeamId = teamId, ProviderInstanceId = instanceId, CredentialId = credentialId,
            ExternalId = repoId.ToString(), NamespacePath = "org", Name = "repo", FullPath = "org/repo",
            DefaultBranch = "main", CloneUrlHttps = cloneUrlHttps, WebUrl = "https://local/org/repo",
        });

        await db.SaveChangesAsync();
        return repoId;
    }

    /// <summary>A file outside every workspace, holding text a model must never be handed. GUID-suffixed; best-effort cleanup.</summary>
    private sealed class OutsideFile : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cs-memory-outside-" + Guid.NewGuid().ToString("N"));

        public OutsideFile()
        {
            Directory.CreateDirectory(_directory);
            Path = System.IO.Path.Combine(_directory, "secret.md");
            File.WriteAllText(Path, $"OUTSIDE-{Guid.NewGuid():N}\n");
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>A bare local remote whose one commit holds the contract's check, an <c>AGENTS.md</c>, a <c>CLAUDE.md</c> committed as a symlink, and any other files and symlinks given. GUID-suffixed; best-effort cleanup.</summary>
    private sealed class BareRemote : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-memory-remote-" + Guid.NewGuid().ToString("N"));
        private readonly string _bare;

        public BareRemote()
        {
            Directory.CreateDirectory(_root);
            _bare = Path.Combine(_root, "remote.git");
        }

        public string Url => new Uri(_bare).AbsoluteUri;

        public async Task SeedAsync(string checkScript, string claudeMdTarget, IReadOnlyDictionary<string, string>? files = null, IReadOnlyDictionary<string, string>? links = null)
        {
            await Git(_root, "init", "--bare", "-b", "main", _bare);

            var seed = Path.Combine(_root, "seed");
            Directory.CreateDirectory(seed);
            await Git(seed, "clone", _bare, seed);
            await Git(seed, "config", "user.email", "test@codespace.dev");
            await Git(seed, "config", "user.name", "Test");
            await Git(seed, "config", "commit.gpgsign", "false");
            await File.WriteAllTextAsync(Path.Combine(seed, "check.sh"), checkScript);
            await File.WriteAllTextAsync(Path.Combine(seed, "AGENTS.md"), "Keep the change small.\n");
            File.CreateSymbolicLink(Path.Combine(seed, "CLAUDE.md"), claudeMdTarget);

            foreach (var (relative, content) in files ?? new Dictionary<string, string>())
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(seed, relative))!);
                await File.WriteAllTextAsync(Path.Combine(seed, relative), content);
            }

            foreach (var (relative, target) in links ?? new Dictionary<string, string>())
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(seed, relative))!);
                File.CreateSymbolicLink(Path.Combine(seed, relative), target);
            }

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
    /// The fake <c>claude</c>: records the directories the argv it was spawned with names after <c>--add-dir</c> —
    /// <c>absent</c> for none, else <c>.</c> for the first, the workspace, and each other one relative to it — and, when
    /// armed to, after <c> ; </c> every glob the pointer rules in its <c>$CLAUDE_CONFIG_DIR</c> carry, file by file; exits 9
    /// when that is not what the test expects of its round, and otherwise drafts <c>feature.txt</c> — running the draft
    /// action as it does, when given one — or writes the revision, when its goal is the executor's revise instruction,
    /// and prints a successful stream-json result. Named and staged with the <see cref="FakeAgentCliMarker"/> markers, so a real-CLI
    /// gate elsewhere in the process sees it for a fake. Arms the process-wide <see cref="ClaudeCodeHarness.CommandEnvVar"/>;
    /// restores it and deletes its directory on dispose.
    /// </summary>
    private sealed class MemoryCheckingFakeCli : IDisposable
    {
        private readonly string? _original;
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "cs-memory" + FakeAgentCliMarker.DirectoryMarker + Guid.NewGuid().ToString("N"));
        private readonly string _launches;
        private readonly string _draft;
        private readonly string _revision;
        private readonly string _draftAction;
        private readonly string _pointers;

        /// <param name="draft">The directories the draft round's argv must add, as the fake records them: <c>absent</c>, <c>.</c> or <c>. pkg</c>.</param>
        /// <param name="revision">The same, for the revision.</param>
        /// <param name="draftAction">A shell command the draft round runs in its workspace, or null for none.</param>
        /// <param name="pointers">Whether each launch's record also carries the globs of the pointer rules in its config home.</param>
        public MemoryCheckingFakeCli(string draft, string revision, string? draftAction, bool pointers = false)
        {
            (_draft, _revision, _draftAction, _pointers) = (draft, revision, draftAction ?? "", pointers ? "1" : "");
            Directory.CreateDirectory(_directory);
            _launches = Path.Combine(_directory, "launches.txt");

            var script = Path.Combine(_directory, FakeAgentCliMarker.ScriptNamePrefix + "claude.sh");
            File.WriteAllText(script, "#!/bin/sh\n" + FakeAgentCliDialect.ClaudeGoalFunction + $$"""
                goal=$(claude_goal)
                memory=absent; first=; listing=0
                for arg in "$@"; do
                  case "$arg" in --add-dir) listing=1; continue ;; --*) listing=0 ;; esac
                  [ "$listing" = 1 ] || continue
                  if [ -z "$first" ]; then first=$arg; memory=.; else memory="$memory ${arg#"$first"/}"; fi
                done
                if [ -n "$FAKE_POINTERS" ]; then
                  globs=$(cat "$CLAUDE_CONFIG_DIR"/{{ClaudeWorkspaceMemory.PointerRulePrefix}}*.md 2>/dev/null | sed -n 's/^  - "\(.*\)"$/\1/p' | tr '\n' ' ')
                  memory="$memory ; ${globs% }"
                fi
                printf '%s\n' "$memory" >> "$FAKE_LAUNCHES"
                case "$goal" in {{AgentRunExecutor.ReviseInstructionPrefix}}*) expected=$FAKE_EXPECT_REVISION ;; *) expected=$FAKE_EXPECT_DRAFT ;; esac
                [ "$memory" = "$expected" ] || { echo "expected the memory directories '$expected', argv: $*" >&2; exit 9; }
                case "$goal" in
                  {{AgentRunExecutor.ReviseInstructionPrefix}}*) printf 'revised\n' > feature.txt ;;
                  *) printf 'draft\n' > feature.txt; [ -z "$FAKE_DRAFT_ACTION" ] || eval "$FAKE_DRAFT_ACTION" ;;
                esac
                printf '%s\n' '{"type":"result","subtype":"success","is_error":false,"result":"done"}'

                """);
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            _original = Environment.GetEnvironmentVariable(ClaudeCodeHarness.CommandEnvVar);
            Environment.SetEnvironmentVariable(ClaudeCodeHarness.CommandEnvVar, script);
        }

        public IReadOnlyDictionary<string, string> Env() => new Dictionary<string, string> { ["FAKE_LAUNCHES"] = _launches, ["FAKE_EXPECT_DRAFT"] = _draft, ["FAKE_EXPECT_REVISION"] = _revision, ["FAKE_DRAFT_ACTION"] = _draftAction, ["FAKE_POINTERS"] = _pointers };

        /// <summary>What each launch's argv said, in order.</summary>
        public IReadOnlyList<string> Launches() => File.Exists(_launches) ? File.ReadAllLines(_launches) : Array.Empty<string>();

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(ClaudeCodeHarness.CommandEnvVar, _original);
            try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort */ }
        }
    }
}
