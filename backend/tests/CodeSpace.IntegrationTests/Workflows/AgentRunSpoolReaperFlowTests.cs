using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// The spool reaper against real Postgres + real directories. Proves it reclaims an OLD TERMINAL run's spool
/// and clears its handle, while NEVER touching a live run's spool (gated on CompletedAt, not age), a still-recent
/// terminal run's spool (inside the retention window), or any directory outside the spool root (the containment
/// guard). The spool root is redirected to an isolated temp dir for the test (Rule 12.2/12.3).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AgentRunSpoolReaperFlowTests : IDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly string? _originalSpoolRoot;
    private readonly string _spoolRoot;

    public AgentRunSpoolReaperFlowTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _originalSpoolRoot = Environment.GetEnvironmentVariable(LocalProcessRunner.SpoolRootEnvVar);
        _spoolRoot = Path.Combine(Path.GetTempPath(), "cs-reaper-it-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable(LocalProcessRunner.SpoolRootEnvVar, _spoolRoot);
    }

    [Fact]
    public async Task Reaps_an_old_terminal_runs_spool_and_clears_its_handle()
    {
        var teamId = await SeedTeamAsync();
        var dir = MakeSpoolDir(Path.Combine(_spoolRoot, Guid.NewGuid().ToString("N")));
        var runId = await SeedTerminalRunWithHandleAsync(teamId, dir, completedAt: DateTimeOffset.UtcNow.AddDays(-2));

        Directory.Exists(dir).ShouldBeTrue("precondition: the spool dir exists");

        int reaped;
        using (var scope = _fixture.BeginScope())
            reaped = await scope.Resolve<IAgentRunSpoolReaper>().ReapAsync(CancellationToken.None);

        reaped.ShouldBeGreaterThanOrEqualTo(1);
        Directory.Exists(dir).ShouldBeFalse("the old terminal run's spool dir is reclaimed");

        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None)).RunnerHandleJson
                .ShouldBeNull("the handle is cleared so the run drops out of the candidate set");
    }

    [Fact]
    public async Task Never_reaps_a_live_runs_spool_however_old_the_dir()
    {
        var teamId = await SeedTeamAsync();
        var dir = MakeSpoolDir(Path.Combine(_spoolRoot, Guid.NewGuid().ToString("N")));

        // Running, NEVER completed (CompletedAt null) → must never be a reap candidate, no matter how long it runs.
        Guid runId;
        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            runId = (await svc.CreateAsync(BuildTask(), teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None)).Id;
            await svc.MarkRunningAsync(runId, CancellationToken.None);
            await svc.SetRunnerHandleAsync(runId, HandleJson(dir), CancellationToken.None);
        }

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunSpoolReaper>().ReapAsync(CancellationToken.None);

        Directory.Exists(dir).ShouldBeTrue("a live run's spool is never touched");
        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None)).RunnerHandleJson
                .ShouldNotBeNull("the live run keeps its handle");
    }

    [Fact]
    public async Task Does_not_reap_a_terminal_run_still_inside_the_retention_window()
    {
        var teamId = await SeedTeamAsync();
        var dir = MakeSpoolDir(Path.Combine(_spoolRoot, Guid.NewGuid().ToString("N")));
        await SeedTerminalRunWithHandleAsync(teamId, dir, completedAt: DateTimeOffset.UtcNow);   // just completed

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunSpoolReaper>().ReapAsync(CancellationToken.None);

        Directory.Exists(dir).ShouldBeTrue("a freshly-completed run's spool is kept until past the retention window");
    }

    [Fact]
    public async Task Never_deletes_a_directory_outside_the_spool_root_but_still_clears_the_handle()
    {
        var teamId = await SeedTeamAsync();

        // A handle whose SpoolDirectory points OUTSIDE the spool root (a corrupt/forged path). The containment
        // guard must refuse to delete it — but the handle is still cleared so the run isn't re-swept forever.
        var outsideDir = MakeSpoolDir(Path.Combine(Path.GetTempPath(), "cs-reaper-outside-" + Guid.NewGuid().ToString("N")));
        var runId = await SeedTerminalRunWithHandleAsync(teamId, outsideDir, completedAt: DateTimeOffset.UtcNow.AddDays(-2));

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunSpoolReaper>().ReapAsync(CancellationToken.None);

        Directory.Exists(outsideDir).ShouldBeTrue("an out-of-root path is NEVER deleted by the reaper");
        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None)).RunnerHandleJson
                .ShouldBeNull("the handle is still cleared so the run doesn't get re-processed every sweep");
    }

    private async Task<Guid> SeedTerminalRunWithHandleAsync(Guid teamId, string spoolDir, DateTimeOffset completedAt)
    {
        Guid runId;
        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IAgentRunService>();
            runId = (await svc.CreateAsync(BuildTask(), teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None)).Id;
            var epoch = await svc.MarkRunningAsync(runId, CancellationToken.None);
            await svc.SetRunnerHandleAsync(runId, HandleJson(spoolDir), CancellationToken.None);
            await svc.CompleteAsync(runId, new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "done" }, epoch, CancellationToken.None);
        }

        // Backdate CompletedAt to control the retention gate (CompleteAsync stamps it to now).
        using (var scope = _fixture.BeginScope())
            await scope.Resolve<CodeSpaceDbContext>().Database
                .ExecuteSqlInterpolatedAsync($"UPDATE agent_run SET completed_at = {completedAt} WHERE id = {runId}");

        return runId;
    }

    private static string HandleJson(string spoolDir) =>
        JsonSerializer.Serialize(new SandboxHandle { Kind = "local", ProcessId = 1, SpoolDirectory = spoolDir, Deadline = DateTimeOffset.UtcNow }, AgentJson.Options);

    private static string MakeSpoolDir(string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "out.log"), "done\n");
        File.WriteAllText(Path.Combine(dir, "exit"), "0");
        return dir;
    }

    private static AgentTask BuildTask() => new() { Goal = "reaper", Harness = "codex-cli", Model = "gpt-5.3-codex" };

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"agent-{userId:N}@test.local", Name = $"agent-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"agent-{teamId:N}", Name = "Agent Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return teamId;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(LocalProcessRunner.SpoolRootEnvVar, _originalSpoolRoot);
        try { Directory.Delete(_spoolRoot, recursive: true); } catch { /* best-effort */ }
        try { foreach (var d in Directory.GetDirectories(Path.GetTempPath(), "cs-reaper-outside-*")) Directory.Delete(d, recursive: true); } catch { /* best-effort */ }
    }
}
