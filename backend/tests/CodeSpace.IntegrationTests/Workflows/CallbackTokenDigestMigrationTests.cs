using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Callbacks;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Workflows;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Migration 0248 is the EXPAND step for callback tokens: it adds <c>token_digest</c> and <c>token_ciphertext</c>, fills the
/// digest for every minted Callback token, and leaves <c>token</c> exactly as it was. A rolling deploy in any role order,
/// and a rollback to the previous build, must keep every issued callback URL working — and the digest must never become a
/// key anywhere.
///
/// <para>Rows are seeded the way each version writes them: the previous version (the token itself, nothing else), this
/// version's engine (token, digest and ciphertext), and the follow-up release that blanks the token (digest and
/// ciphertext only). "A previous-version pod" is <see cref="PreviousVersionResumeAsync"/>: the callback lookup every
/// release before this change ran, verbatim, followed by the resume it hands the wait to.</para>
///
/// <para>The replay commits on the shared fixture database. It only fills a digest a Callback row lacks, with the value
/// this version would write, so a sibling test's callback is unaffected.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class CallbackTokenDigestMigrationTests
{
    private const string MigrationFile = "0248_workflow_callback_token_digest.sql";

    private readonly PostgresFixture _fixture;

    public CallbackTokenDigestMigrationTests(PostgresFixture fixture) { _fixture = fixture; }

    // ── The replay over rows the previous version wrote ──────────────────────────

    [Fact]
    public async Task Replaying_0248_over_previous_version_rows_adds_the_digest_and_leaves_every_token_as_it_was()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSuspendedRunAsync(teamId, userId);
        var shared = Guid.NewGuid().ToString("N");
        var pending = await AddPreviousVersionWaitAsync(runId, "cb", WorkflowWaitKinds.Callback, Guid.NewGuid().ToString("N"));
        var resolved = await AddPreviousVersionWaitAsync(runId, "cb-done", WorkflowWaitKinds.Callback, Guid.NewGuid().ToString("N"), WorkflowWaitStatuses.Resolved);
        var action = await AddPreviousVersionWaitAsync(runId, "act", WorkflowWaitKinds.Action, shared);
        var timer = await AddPreviousVersionWaitAsync(runId, "sleep", WorkflowWaitKinds.Timer, shared);
        var before = await LoadWaitsAsync(pending, resolved, action, timer);

        await ReplayMigrationAsync();
        var once = await LoadWaitsAsync(pending, resolved, action, timer);

        await ReplayMigrationAsync();
        var twice = await LoadWaitsAsync(pending, resolved, action, timer);

        foreach (var (row, original) in once.Zip(before))
        {
            row.Token.ShouldBe(original.Token, "the token column is untouched: a previous-version pod compares it verbatim");
            row.TokenCiphertext.ShouldBeNull("SQL cannot reach the environment-held key");
        }
        once[0].TokenDigest.ShouldBe(WorkflowCallbackTokenSealer.Digest(before[0].Token), "SQL's encode(sha256(...), 'hex') must equal the digest the application looks up");
        once[1].TokenDigest.ShouldBe(WorkflowCallbackTokenSealer.Digest(before[1].Token), "a closed callback gets its digest too, so the follow-up can blank every Callback token");
        once[2].TokenDigest.ShouldBeNull("an Action token is looked up verbatim by its authenticated verb");
        once[3].TokenDigest.ShouldBeNull();
        twice.Select(Snapshot).ShouldBe(once.Select(Snapshot), "a second run changes nothing");
    }

    [Fact]
    public async Task A_url_issued_before_0248_resumes_on_this_version_and_can_still_be_revealed()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSuspendedRunAsync(teamId, userId);
        var issued = Guid.NewGuid().ToString("N");
        var waitId = await AddPreviousVersionWaitAsync(runId, "cb", WorkflowWaitKinds.Callback, issued);

        await ReplayMigrationAsync();

        (await RevealAsync(runId, waitId, teamId, userId)).Path.ShouldBe(WorkflowCallbackUrlService.RoutePrefix + issued, "the token column still holds it, so the run page can show the URL again");
        (await ResumeAsync(issued)).ShouldBeTrue("the URL an external system already holds keeps resuming the run");
        (await LoadWaitAsync(waitId)).Status.ShouldBe(WorkflowWaitStatuses.Resolved);
    }

    // ── A previous-version pod against the migrated database ─────────────────────

    [Fact]
    public async Task A_previous_version_pod_resumes_a_url_issued_before_0248_and_refuses_its_digest()
    {
        // A rolling deploy: the first new pod of either role has run 0248; an API pod on the previous build still serves.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSuspendedRunAsync(teamId, userId);
        var issued = Guid.NewGuid().ToString("N");
        var waitId = await AddPreviousVersionWaitAsync(runId, "cb", WorkflowWaitKinds.Callback, issued);

        await ReplayMigrationAsync();
        var digest = (await LoadWaitAsync(waitId)).TokenDigest!;

        (await PreviousVersionResumeAsync(digest)).ShouldBeFalse("the digest a database reader holds must not be a key on a previous-version pod");
        (await PreviousVersionResumeAsync(issued)).ShouldBeTrue("the previous build still finds the issued token in the token column");
        (await LoadWaitAsync(waitId)).Status.ShouldBe(WorkflowWaitStatuses.Resolved);
    }

    [Fact]
    public async Task After_a_rollback_the_previous_version_shows_and_resumes_a_url_this_version_minted_and_refuses_its_digest()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await ParkOnCallbackAsync(teamId, userId);
        var wait = await LoadCallbackWaitAsync(runId);
        var issued = TokenOf(await RevealAsync(runId, wait.Id, teamId, userId));

        wait.Token.ShouldBe(issued, "the previous build's run page builds the URL from the token column, so a rolled-back UI still shows the URL that works");
        wait.TokenDigest.ShouldBe(WorkflowCallbackTokenSealer.Digest(issued));
        (await PreviousVersionResumeAsync(wait.TokenDigest!)).ShouldBeFalse("the digest is not a key on the previous build either");
        (await PreviousVersionResumeAsync(issued)).ShouldBeTrue("a URL issued on this version keeps working after a rollback");
        (await LoadWaitAsync(wait.Id)).Status.ShouldBe(WorkflowWaitStatuses.Resolved);
    }

    [Fact]
    public async Task A_url_a_previous_version_pod_parks_after_0248_resumes_and_reveals_on_this_version()
    {
        // A previous-version worker during the rollout, or the whole fleet after a rollback, parks a callback the old way —
        // no digest, no ciphertext. Rolling forward again must keep it working.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSuspendedRunAsync(teamId, userId);
        var plaintext = Guid.NewGuid().ToString("N");
        var waitId = await AddPreviousVersionWaitAsync(runId, "cb", WorkflowWaitKinds.Callback, plaintext);

        (await RevealAsync(runId, waitId, teamId, userId)).Path.ShouldBe(WorkflowCallbackUrlService.RoutePrefix + plaintext);
        (await ResumeAsync(plaintext)).ShouldBeTrue("this version also looks a minted-shape token up in the token column");
    }

    // ── Rolling the follow-up release back onto this one ─────────────────────────

    [Fact]
    public async Task A_url_whose_token_a_later_release_blanked_resumes_by_its_digest_and_reveals_from_its_ciphertext()
    {
        // The follow-up release blanks the token column once every row has a digest and a ciphertext. Rolled back onto
        // this version, such a row must still work — which is why this version already looks waits up by digest.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSuspendedRunAsync(teamId, userId);
        var minted = Mint();
        var waitId = await AddBlankedCallbackWaitAsync(runId, minted, minted.Ciphertext);

        (await PreviousVersionResumeAsync(minted.Token)).ShouldBeFalse("precondition: nothing but the digest locates this row");
        (await RevealAsync(runId, waitId, teamId, userId)).Path.ShouldBe(WorkflowCallbackUrlService.RoutePrefix + minted.Token);
        (await ResumeAsync(minted.Digest)).ShouldBeFalse("the digest presented as a token is not a key on this version");
        (await ResumeAsync(minted.Token)).ShouldBeTrue("this version resumes by the digest of the presented token");
    }

    [Fact]
    public async Task A_row_with_neither_its_token_nor_a_ciphertext_still_resumes_but_cannot_be_revealed()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSuspendedRunAsync(teamId, userId);
        var minted = Mint();
        var waitId = await AddBlankedCallbackWaitAsync(runId, minted, ciphertext: null);

        var refused = await Should.ThrowAsync<WorkflowCallbackUrlUnrecoverableException>(() => RevealAsync(runId, waitId, teamId, userId));
        refused.Kind.ShouldBe(CodeSpace.Messages.Failures.FailureKind.Conflict);
        (await ResumeAsync(minted.Token)).ShouldBeTrue("the URL already handed out still resumes the run");
    }

    // ─── The previous version ───────────────────────────────────────────────────

    /// <summary>
    /// A callback POST on a pod running any build before this change: <c>WorkflowResumeService.ResumeByCallbackTokenAsync</c>
    /// as of 270ce6c1d (unchanged since callbacks shipped) looks the wait up with
    /// <c>w.Token == token &amp;&amp; w.Status == Pending &amp;&amp; w.WaitKind == Callback</c> — the SQL below, which reads
    /// no column the previous build lacks — and hands the wait to <c>ResumeCoreAsync(wait.RunId, body, wait.Id)</c>, which is
    /// <see cref="IWorkflowResumeService.ResumeWaitAsync"/>. Builds before a0eb90933 route any segment to it, so a
    /// 64-character digest reaches this lookup there.
    /// </summary>
    private async Task<bool> PreviousVersionResumeAsync(string presented)
    {
        const string previousVersionLookup = "SELECT id, run_id FROM workflow_run_wait WHERE token = @token AND status = 'Pending' AND wait_kind = 'Callback' LIMIT 1";

        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(previousVersionLookup, connection);
        command.Parameters.AddWithValue("token", presented);
        await using var reader = await command.ExecuteReaderAsync();

        if (!await reader.ReadAsync()) return false;

        var (waitId, runId) = (reader.GetGuid(0), reader.GetGuid(1));

        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IWorkflowResumeService>().ResumeWaitAsync(runId, waitId, """{"via":"previous-version"}""", CancellationToken.None);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task ReplayMigrationAsync()
    {
        var sql = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Persistence", "DbUpFiles", MigrationFile));

        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<bool> ResumeAsync(string token)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IMediator>().Send(new ResumeWorkflowCallbackCommand { Token = token, Body = "{}" });
    }

    private async Task<WorkflowCallbackUrl> RevealAsync(Guid runId, Guid waitId, Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new RevealWorkflowCallbackUrlCommand { RunId = runId, WaitId = waitId });
    }

    private static string TokenOf(WorkflowCallbackUrl url)
    {
        url.Path.ShouldStartWith(WorkflowCallbackUrlService.RoutePrefix);
        return url.Path[WorkflowCallbackUrlService.RoutePrefix.Length..];
    }

    private SealedCallbackToken Mint()
    {
        using var scope = _fixture.BeginScope();
        return scope.Resolve<WorkflowCallbackTokenSealer>().Mint();
    }

    private static (string Token, string? TokenDigest, string? TokenCiphertext, string Status) Snapshot(WorkflowRunWait w) => (w.Token, w.TokenDigest, w.TokenCiphertext, w.Status);

    private async Task<WorkflowRunWait> LoadWaitAsync(Guid waitId) => (await LoadWaitsAsync(waitId)).Single();

    private async Task<List<WorkflowRunWait>> LoadWaitsAsync(params Guid[] waitIds)
    {
        using var scope = _fixture.BeginScope();
        var rows = await scope.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking().Where(w => waitIds.Contains(w.Id)).ToListAsync();

        return waitIds.Select(id => rows.Single(w => w.Id == id)).ToList();
    }

    private async Task<WorkflowRunWait> LoadCallbackWaitAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.Callback);
    }

    private async Task<Guid> SeedSuspendedRunAsync(Guid teamId, Guid userId)
    {
        var workflowId = await CreateWorkflowAsync(teamId, userId, WorkflowsTestSeed.MinimalDefinition());
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        using var scope = _fixture.BeginScope();
        await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.Where(r => r.Id == runId).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, WorkflowRunStatus.Suspended));

        return runId;
    }

    /// <summary>trigger → flow.wait_callback → terminal, walked by the real engine, which parks the wait and mints its token.</summary>
    private async Task<Guid> ParkOnCallbackAsync(Guid teamId, Guid userId)
    {
        var workflowId = await CreateWorkflowAsync(teamId, userId, new WorkflowDefinition
        {
            SchemaVersion = 1,
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "callback", TypeKey = "flow.wait_callback", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            },
            Edges = new List<EdgeDefinition> { new() { From = "start", To = "callback" }, new() { From = "callback", To = "end" } },
        });
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);

        return runId;
    }

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition definition)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "callback-digest-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = definition,
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    /// <summary>A wait row exactly as the previous version writes one: the token verbatim, no digest, no ciphertext.</summary>
    private Task<Guid> AddPreviousVersionWaitAsync(Guid runId, string nodeId, string waitKind, string token, string status = WorkflowWaitStatuses.Pending) =>
        AddWaitAsync(new WorkflowRunWait { RunId = runId, NodeId = nodeId, WaitKind = waitKind, Token = token, Status = status });

    /// <summary>A Callback row as the follow-up release leaves it once it blanks the token column: digest and, when sealed, ciphertext.</summary>
    private Task<Guid> AddBlankedCallbackWaitAsync(Guid runId, SealedCallbackToken minted, string? ciphertext) =>
        AddWaitAsync(new WorkflowRunWait { RunId = runId, NodeId = "cb", WaitKind = WorkflowWaitKinds.Callback, Token = "", TokenDigest = minted.Digest, TokenCiphertext = ciphertext, Status = WorkflowWaitStatuses.Pending });

    private async Task<Guid> AddWaitAsync(WorkflowRunWait wait)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        wait.Id = Guid.NewGuid();
        wait.CreatedAt = DateTimeOffset.UtcNow;
        db.WorkflowRunWait.Add(wait);
        await db.SaveChangesAsync();

        return wait.Id;
    }
}
