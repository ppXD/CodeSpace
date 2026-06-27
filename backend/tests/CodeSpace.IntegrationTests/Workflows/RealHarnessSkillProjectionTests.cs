using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using CodeSpace.Core.Services.Agents.Harnesses.Codex;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// High-fidelity proof (Rule 12 tier-🟢) that a persona's skill actually REACHES the CLI: the REAL
/// <see cref="IAgentRunExecutor"/> + REAL <see cref="CodexHarness"/>/<see cref="ClaudeCodeHarness"/> + REAL
/// <c>LocalProcessRunner</c> materialize the projected <c>SKILL.md</c> into the per-run config home, and a
/// skill-AWARE fake CLI — standing in for the unavailable codex/claude binary — completes the run ONLY if it can
/// read that <c>SKILL.md</c> (carrying a unique token) at exactly the path its real counterpart scans
/// (<c>$CODEX_HOME/skills</c> / <c>$CLAUDE_CONFIG_DIR/skills</c>). So "run Succeeded" is a binary proof the
/// projection landed where the CLI looks; a missing/misplaced file fails the run. No real model needed — the
/// live-binary-with-real-model run is the gated tier (a follow-up; this is the always-on catch-net).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class RealHarnessSkillProjectionTests
{
    private readonly PostgresFixture _fixture;

    public RealHarnessSkillProjectionTests(PostgresFixture fixture) { _fixture = fixture; }

    [Theory]
    [InlineData("codex-cli")]
    [InlineData("claude-code")]
    public async Task A_bound_skill_is_materialized_where_the_real_cli_scans(string harnessKind)
    {
        if (OperatingSystem.IsWindows()) return;   // the skill-aware CLI is a /bin/sh script

        var token = "SKILLPROOF" + Guid.NewGuid().ToString("N")[..8];
        using var cli = new SkillAwareCli(harnessKind, token);

        var teamId = await SeedTeamAsync();
        var skill = new AgentSkill { Slug = "proof-skill", Description = "Use always.", Body = "Marker: " + token };
        var runId = await CreateRunWithSkillAsync(teamId, harnessKind, skill, cli.Env());

        await ExecuteRealAsync(runId);

        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);

        run.Status.ShouldBe(AgentRunStatus.Succeeded,
            customMessage: $"{harnessKind}: the run only completes if the skill-aware CLI found proof-skill/SKILL.md (token {token}) under its config-home skills dir — i.e. the REAL executor+harness+runner materialized the projected skill exactly where {harnessKind}'s native loader scans. A Failed run means the SKILL.md was missing or misplaced.");
    }

    private async Task<Guid> CreateRunWithSkillAsync(Guid teamId, string harnessKind, AgentSkill skill, IReadOnlyDictionary<string, string> env)
    {
        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().CreateAsync(
            new AgentTask { Goal = "demonstrate the skill", Harness = harnessKind, Model = null, Skills = new[] { skill }, Environment = env, TimeoutSeconds = 120 },
            teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None);
        return run.Id;
    }

    private async Task ExecuteRealAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IAgentRunExecutor>().ExecuteAsync(runId, CancellationToken.None);
    }

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"skillexec-{userId:N}@test.local", Name = $"skillexec-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"skillexec-{teamId:N}", Name = "Skill Exec Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return teamId;
    }

    /// <summary>
    /// A skill-AWARE fake CLI: a <c>/bin/sh</c> script that reads its config-home skills dir
    /// (<c>CLAUDE_CONFIG_DIR</c> or <c>CODEX_HOME</c> — whichever the harness set) and emits the harness-appropriate
    /// happy stream ONLY if a SKILL.md containing the unique token is present; otherwise it exits non-zero so the run
    /// fails. Points the harness's <c>CommandEnvVar</c> at itself; restores it + cleans up on dispose.
    /// </summary>
    private sealed class SkillAwareCli : IDisposable
    {
        private readonly string _envVar;
        private readonly string? _original;
        private readonly string _dir;
        private readonly string _token;
        private readonly string _fixturePath;

        public SkillAwareCli(string harnessKind, string token)
        {
            _token = token;

            string fixture;
            (_envVar, fixture) = harnessKind switch
            {
                "codex-cli" => (CodexHarness.CommandEnvVar, "{\"type\":\"agent_message\",\"message\":\"skill-confirmed\"}\n{\"type\":\"task_complete\",\"message\":\"completed\"}\n"),
                "claude-code" => (ClaudeCodeHarness.CommandEnvVar, "{\"type\":\"result\",\"subtype\":\"success\",\"result\":\"skill-confirmed\",\"is_error\":false}\n"),
                _ => throw new ArgumentOutOfRangeException(nameof(harnessKind), harnessKind, null),
            };

            _dir = Path.Combine(Path.GetTempPath(), "cs-skillcli-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);

            _fixturePath = Path.Combine(_dir, "found.jsonl");
            File.WriteAllText(_fixturePath, fixture);

            var script = Path.Combine(_dir, "skill-cli.sh");
            File.WriteAllText(script,
                "#!/bin/sh\n" +
                "CFG=\"${CLAUDE_CONFIG_DIR:-$CODEX_HOME}\"\n" +
                "if [ -n \"$CFG\" ] && grep -rq \"$SKILL_TOKEN\" \"$CFG/skills\" 2>/dev/null; then\n" +
                "  cat \"$FOUND_FIXTURE\"\n" +
                "  exit 0\n" +
                "fi\n" +
                "echo \"skill not materialized under $CFG/skills\" 1>&2\n" +
                "exit 5\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            _original = Environment.GetEnvironmentVariable(_envVar);
            Environment.SetEnvironmentVariable(_envVar, script);
        }

        /// <summary>The run environment the spawned script reads — the token to look for + the fixture to emit when found.</summary>
        public IReadOnlyDictionary<string, string> Env() => new Dictionary<string, string>
        {
            ["SKILL_TOKEN"] = _token,
            ["FOUND_FIXTURE"] = _fixturePath,
        };

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(_envVar, _original);
            try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
        }
    }
}
