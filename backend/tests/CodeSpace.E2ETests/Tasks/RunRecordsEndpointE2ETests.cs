using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using CodeSpace.Api.Http;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Auth;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Failures;
using CodeSpace.Messages.Tasks.Trace;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Shouldly;

namespace CodeSpace.E2ETests.Tasks;

/// <summary>
/// E2E coverage for <c>GET /api/workflows/runs/{runId}/records</c> through the REAL ASP.NET pipeline — JWT auth, the
/// X-Team-Id team-scope, route binding, the controller, the <c>IRunRecordReader</c> dumping the durable
/// <c>workflow_run_record</c> ledger UNFILTERED. A run + its lifecycle records are seeded directly, then the raw audit
/// is fetched over real HTTP and asserted; a foreign / absent run returns 404 (team-scoped, fail-closed).
///
/// <para>Tier: 🟢 High-fidelity — real app host + real Postgres + the real reader reading the durable substrate.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class RunRecordsEndpointE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private readonly TaskLaunchApiFactory _factory;

    public RunRecordsEndpointE2ETests(TaskLaunchApiFactory factory) { _factory = factory; }

    [Fact]
    public async Task Get_records_returns_the_raw_unfiltered_ledger()
    {
        var (userId, teamId) = await SeedTeamMembershipAsync();
        var runId = await SeedRunWithRecordsAsync(teamId);

        var response = await SendAsync(HttpMethod.Get, $"/api/workflows/runs/{runId}/records", userId, teamId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, customMessage: await DescribeFailureAsync(response));

        var body = await response.Content.ReadFromJsonAsync<RunRecordsResponse>();
        body.ShouldNotBeNull();
        body!.RunId.ShouldBe(runId);
        body.RunStatus.ShouldBe(nameof(WorkflowRunStatus.Failure));
        body.Records.Select(r => r.RecordType).ShouldBe(new[]
        {
            WorkflowRunRecordTypes.RunStarted, WorkflowRunRecordTypes.ScopeResolved, WorkflowRunRecordTypes.Log,
            WorkflowRunRecordTypes.NodeStarted, WorkflowRunRecordTypes.RunFailed,
        }, "the raw ledger projects over real HTTP in Sequence order — the scope + log records the narrative drops are present");

        body.Records.Single(r => r.RecordType == WorkflowRunRecordTypes.Log).PayloadJson
            .ShouldContain("noise", customMessage: "the raw payload is carried verbatim, not a derived narrative title");
    }

    [Fact]
    public async Task Get_records_for_an_absent_run_is_404()
    {
        var (userId, teamId) = await SeedTeamMembershipAsync();

        var response = await SendAsync(HttpMethod.Get, $"/api/workflows/runs/{Guid.NewGuid()}/records", userId, teamId);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, "a run that isn't the team's is 404 — team-scoped, no existence leak");
    }

    [Fact]
    public async Task Get_record_page_exposes_bounded_tail_and_older_keysets_and_rejects_ambiguous_cursors()
    {
        var (userId, teamId) = await SeedTeamMembershipAsync();
        var runId = await SeedRunWithRecordsAsync(teamId);

        var tailResponse = await SendAsync(HttpMethod.Get, $"/api/workflows/runs/{runId}/records/page?limit=2", userId, teamId);
        tailResponse.StatusCode.ShouldBe(HttpStatusCode.OK, customMessage: await DescribeFailureAsync(tailResponse));
        var tail = await tailResponse.Content.ReadFromJsonAsync<RunRecordPageResponse>();

        tail.ShouldNotBeNull();
        tail!.Mode.ShouldBe(RunRecordPageModes.Tail);
        tail.Records.Select(row => row.RecordType).ShouldBe(new[] { WorkflowRunRecordTypes.NodeStarted, WorkflowRunRecordTypes.RunFailed });
        tail.Records.ShouldAllBe(row => row.RecordId != Guid.Empty && row.PayloadState == RunRecordPagePayloadStates.Deferred && row.PayloadContentType == "application/json");
        typeof(RunRecordPageItem).GetProperty("PayloadJson").ShouldBeNull("the bounded page wire must be structurally body-free");
        tail.NextBeforeSequence.ShouldBe(tail.Records[0].Sequence);
        tail.NextAfterSequence.ShouldBeNull();

        var olderResponse = await SendAsync(HttpMethod.Get, $"/api/workflows/runs/{runId}/records/page?beforeSequence={tail.NextBeforeSequence}&limit=2", userId, teamId);
        olderResponse.StatusCode.ShouldBe(HttpStatusCode.OK, customMessage: await DescribeFailureAsync(olderResponse));
        var older = await olderResponse.Content.ReadFromJsonAsync<RunRecordPageResponse>();

        older.ShouldNotBeNull();
        older!.Mode.ShouldBe(RunRecordPageModes.Older);
        older.Records.Select(row => row.RecordType).ShouldBe(new[] { WorkflowRunRecordTypes.ScopeResolved, WorkflowRunRecordTypes.Log });
        older.NextBeforeSequence.ShouldNotBeNull();

        var invalid = await SendAsync(HttpMethod.Get, $"/api/workflows/runs/{runId}/records/page?beforeSequence=1&afterSequence=1", userId, teamId);
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Read_record_payload_exposes_exact_identity_and_lossless_bounded_canonical_jsonb_bytes()
    {
        var (userId, teamId) = await SeedTeamMembershipAsync();
        var runId = await SeedRunWithRecordsAsync(teamId);
        Guid recordId;
        long sequence;
        string expected;
        using (var scope = _factory.Services.CreateScope())
        {
            var source = await scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>().WorkflowRunRecord.AsNoTracking()
                .Where(record => record.RunId == runId && record.RecordType == WorkflowRunRecordTypes.Log)
                .Select(record => new { record.Id, record.Sequence, record.PayloadJson }).SingleAsync();
            recordId = source.Id;
            sequence = source.Sequence;
            expected = source.PayloadJson;
        }

        using var output = new MemoryStream();
        long offset = 0;
        do
        {
            var response = await SendAsync(HttpMethod.Get, $"/api/workflows/runs/{runId}/records/{recordId}/payload?offsetBytes={offset}&limitBytes=4", userId, teamId);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, customMessage: await DescribeFailureAsync(response));
            response.Content.Headers.ContentType!.MediaType.ShouldBe("application/octet-stream");
            response.Headers.GetValues(RunRecordPayloadHttpHeaders.RunId).ShouldHaveSingleItem().ShouldBe(runId.ToString());
            response.Headers.GetValues(RunRecordPayloadHttpHeaders.RecordId).ShouldHaveSingleItem().ShouldBe(recordId.ToString());
            response.Headers.GetValues(RunRecordPayloadHttpHeaders.Sequence).ShouldHaveSingleItem().ShouldBe(sequence.ToString());
            response.Headers.GetValues(RunRecordPayloadHttpHeaders.Offset).ShouldHaveSingleItem().ShouldBe(offset.ToString());
            response.Headers.GetValues(RunRecordPayloadHttpHeaders.ContentType).ShouldHaveSingleItem().ShouldBe("application/json");
            response.Headers.GetValues(RunRecordPayloadHttpHeaders.TotalBytes).ShouldHaveSingleItem().ShouldBe(Encoding.UTF8.GetByteCount(expected).ToString());

            var bytes = await response.Content.ReadAsByteArrayAsync();
            bytes.Length.ShouldBeInRange(1, 4);
            await output.WriteAsync(bytes);
            offset += bytes.Length;
        } while (offset < Encoding.UTF8.GetByteCount(expected));

        Encoding.UTF8.GetString(output.ToArray()).ShouldBe(expected);
    }

    [Theory]
    [InlineData(false)]   // an API client's Authorization header
    [InlineData(true)]    // the browser's HttpOnly session cookie — the SPA's tail sends no Authorization header at all
    public async Task Get_records_stream_tails_the_ledger_as_server_sent_events_and_ends_at_the_terminal_record(bool viaSessionCookie)
    {
        var (userId, teamId) = await SeedTeamMembershipAsync();
        var runId = await SeedRunWithRecordsAsync(teamId);   // ends with run.failed → the terminal record closes the stream itself

        using var client = _factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);   // safety net — a correct tail closes the stream at the terminal record
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/workflows/runs/{runId}/records/stream?after=0");
        if (viaSessionCookie) request.Headers.Add("Cookie", $"{SessionCookie.Name}={MintToken(userId)}");
        else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MintToken(userId));
        request.Headers.Add("X-Team-Id", teamId.ToString());

        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, customMessage: await DescribeFailureAsync(response));
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/event-stream", "the tail is a Server-Sent Events stream");

        var body = await response.Content.ReadAsStringAsync();   // completes when the terminal record closes the stream

        body.ShouldContain("event: " + WorkflowRunRecordTypes.RunStarted, customMessage: "the lifecycle records stream as SSE events");
        body.ShouldContain("event: " + WorkflowRunRecordTypes.RunFailed, customMessage: "the terminal record is streamed");
        body.ShouldContain("id: ", customMessage: "each frame carries its Sequence as the SSE id — Last-Event-ID / ?after= resume");
        body.ShouldContain("\ndata: {", customMessage: "each frame carries a JSON data payload");
        body.TrimEnd().ShouldEndWith("}", customMessage: "the stream ends right after the terminal record's frame — it does not hang open");
    }

    [Fact]
    public async Task Get_records_stream_for_a_foreign_run_yields_an_empty_stream()
    {
        var (_, teamA) = await SeedTeamMembershipAsync();
        var runId = await SeedRunWithRecordsAsync(teamA);
        var (userB, teamB) = await SeedTeamMembershipAsync();

        using var client = _factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/workflows/runs/{runId}/records/stream?after=0");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MintToken(userB));
        request.Headers.Add("X-Team-Id", teamB.ToString());

        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.ShouldBeEmpty("a foreign run streams NO events — the run precheck is the tenancy boundary, no ledger leak");
    }

    /// <summary>
    /// The stream used to trust <c>X-Team-Id</c> as written: it was the one action that bypassed the mediator, so no
    /// membership behavior ever ran, and the run precheck only compared the run's team to the header. Anyone signed in
    /// who knew a team id and a run id live-tailed that run — an outsider, a member removed a minute ago, the members
    /// of a deleted team. Each must now be refused with a real 403 before a single frame (or the event-stream content
    /// type) is written. The control in each case proves the run itself streams for someone with standing, so the 403
    /// is the caller's standing and not a broken fixture.
    /// </summary>
    [Theory]
    [InlineData(LostStanding.NeverAMember)]
    [InlineData(LostStanding.RemovedFromTeam)]
    [InlineData(LostStanding.TeamDeleted)]
    public async Task Get_records_stream_is_403_before_any_frame_for_a_caller_without_standing_on_the_header_team(LostStanding standing)
    {
        var (ownerId, teamId) = await SeedTeamMembershipAsync();
        var runId = await SeedRunWithRecordsAsync(teamId);

        var callerId = await ArrangeCallerWithoutStandingAsync(standing, ownerId, teamId, runId);

        using var response = await OpenStreamAsync(callerId, teamId, runId);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, customMessage: $"{standing}: the stream must refuse before it opens; got {(int)response.StatusCode} {response.Content.Headers.ContentType} body: {body}");
        response.Content.Headers.ContentType?.MediaType.ShouldNotBe("text/event-stream", $"{standing}: a refusal is a JSON error, never an opened event stream");
        body.ShouldContain(FailureCodes.Forbidden, customMessage: $"{standing}: the refusal is the standard tenant denial");
        body.ShouldNotContain("noise", customMessage: $"{standing}: no ledger payload reaches a caller without standing");
        body.ShouldNotContain("event: ", customMessage: $"{standing}: no SSE frame is written");
    }

    /// <summary>
    /// Refusing at connect is not enough on its own. A run waiting on a decision stays non-terminal for days, and a tail
    /// opened while the caller still had standing used to keep delivering every record written after they lost it —
    /// removed from the team, the team deleted, their sessions ended by a password change, or their token simply aged
    /// out — while every other door on the run already answered 403 or 401. Each such stream must now end without the
    /// later record. The Kept row proves the same record and the terminal one DO arrive for a member who still holds
    /// standing, so their absence is the re-check and not a broken fixture or a stream that was never live. The stream
    /// closes cleanly, which the Room reads as "stop streaming", so the last assertion pins what the Room's 2s poll then
    /// hears with the same token.
    /// </summary>
    [Theory]
    [InlineData(StandingChange.Kept, true, HttpStatusCode.OK)]
    [InlineData(StandingChange.RemovedFromTeam, false, HttpStatusCode.Forbidden)]
    [InlineData(StandingChange.TeamDeleted, false, HttpStatusCode.Forbidden)]
    [InlineData(StandingChange.SessionRevoked, false, HttpStatusCode.Unauthorized)]
    [InlineData(StandingChange.TokenExpired, false, HttpStatusCode.Unauthorized)]
    public async Task An_open_stream_delivers_nothing_written_after_the_caller_loses_standing(StandingChange change, bool stillDelivered, HttpStatusCode pollAfterwards)
    {
        var (ownerId, teamId) = await SeedTeamMembershipAsync();
        var memberId = await AddMemberAsync(teamId, TeamRole.Member);
        var runId = await SeedSuspendedRunAsync(teamId);   // non-terminal, so nothing but the re-check can end this tail
        var memberToken = change == StandingChange.TokenExpired ? MintShortLivedToken(memberId) : MintToken(memberId);

        using var response = await OpenStreamAsync(memberToken, teamId, runId);
        await ShouldHaveOpenedAsync(response, $"{change}: the member opens the tail while still holding standing");

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        await ReadUntilAsync(reader, SuspendedRunMarker, $"{change}: the open stream delivers the record that existed before anything changed");

        await ChangeStandingAsync(change, ownerId, memberId, teamId);

        await AppendRecordAsync(runId, WorkflowRunRecordTypes.Log, $$"""{"level":"info","message":"{{LaterRecordMarker}}"}""");
        await AppendRecordAsync(runId, WorkflowRunRecordTypes.RunFailed, """{"error":"done"}""");   // a stream still tailing closes here

        var remainder = await ReadToEndAsync(reader, change);

        remainder.Contains(LaterRecordMarker).ShouldBe(stillDelivered, customMessage: $"{change}: a record written after the change reaches the open stream only while standing holds; remainder: {remainder}");
        remainder.Contains("event: " + WorkflowRunRecordTypes.RunFailed).ShouldBe(stillDelivered, customMessage: $"{change}: the stream ends by the re-check, before the terminal record, once standing is gone; remainder: {remainder}");

        using var poll = await SendAsync(HttpMethod.Get, $"/api/workflows/runs/{runId}/records", memberToken, teamId);
        poll.StatusCode.ShouldBe(pollAfterwards, customMessage: $"{change}: the Room's fallback poll, sent with the stream's token, answers as every other door does");
    }

    public enum StandingChange { Kept, RemovedFromTeam, TeamDeleted, SessionRevoked, TokenExpired }

    private const string SuspendedRunMarker = "written before the change";
    private const string LaterRecordMarker = "written after the change";
    private const string MemberPassword = "correct-horse-battery";

    /// <summary>How long a short-lived token stays accepted once minted. Long enough to open the stream and read its first record.</summary>
    private static readonly TimeSpan ShortTokenWindow = TimeSpan.FromSeconds(6);

    /// <summary>A real token whose <c>exp</c> is already past but still inside the handler's clock skew, so it is accepted for <see cref="ShortTokenWindow"/> more.</summary>
    private static string MintShortLivedToken(Guid userId) => TestToken.Mint(userId, TestToken.SeedStamp, DateTime.UtcNow - BearerTokenLifetime.ClockSkew + ShortTokenWindow);

    /// <summary>Each change goes through the door an operator would use: the member-removal and change-password endpoints, the soft delete there is no endpoint for, and for expiry nothing at all — time passes.</summary>
    private async Task ChangeStandingAsync(StandingChange change, Guid ownerId, Guid memberId, Guid teamId)
    {
        switch (change)
        {
            case StandingChange.Kept:
                return;
            case StandingChange.RemovedFromTeam:
            {
                using var removal = await SendAsync(HttpMethod.Delete, $"/api/teams/members/{memberId}", ownerId, teamId);
                removal.StatusCode.ShouldBe(HttpStatusCode.NoContent, customMessage: "the owner removes the member through the real endpoint: " + await removal.Content.ReadAsStringAsync());
                return;
            }
            case StandingChange.TeamDeleted:
                await SoftDeleteTeamAsync(teamId);
                return;
            case StandingChange.SessionRevoked:
            {
                var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/change-password") { Content = JsonContent.Create(new { currentPassword = MemberPassword, newPassword = "a-brand-new-passphrase" }) };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MintToken(memberId));

                using var changed = await _factory.CreateClient().SendAsync(request);
                changed.StatusCode.ShouldBe(HttpStatusCode.OK, customMessage: "the member changes their password, which ends every session minted before it: " + await changed.Content.ReadAsStringAsync());
                return;
            }
            case StandingChange.TokenExpired:
                await Task.Delay(ShortTokenWindow + TimeSpan.FromSeconds(1));
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(change), change, null);
        }
    }

    /// <summary>Reads the body only on a refusal: an opened tail on a non-terminal run never ends, so reading it here would hang the test.</summary>
    private static async Task ShouldHaveOpenedAsync(HttpResponseMessage response, string because)
    {
        if (response.StatusCode == HttpStatusCode.OK) return;

        throw new ShouldAssertException($"{because}; expected 200 but got {(int)response.StatusCode}; body: {await response.Content.ReadAsStringAsync()}");
    }

    private static async Task ReadUntilAsync(StreamReader reader, string marker, string because)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var seen = new StringBuilder();

        while (true)
        {
            var line = await reader.ReadLineAsync(timeout.Token) ?? throw new ShouldAssertException($"{because}: the stream ended before '{marker}'. Frames so far: {seen}");
            seen.AppendLine(line);

            if (line.Contains(marker)) return;
        }
    }

    private static async Task<string> ReadToEndAsync(StreamReader reader, StandingChange change)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        try
        {
            return await reader.ReadToEndAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            throw new ShouldAssertException($"{change}: the open stream neither closed nor reached the terminal record within 20s. A terminal record was appended, so a live tail closes on it; a hang means the tail stopped polling. Check RunRecordStreamer.TailAsync.");
        }
    }

    private async Task<Guid> SeedSuspendedRunAsync(Guid teamId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var t = DateTimeOffset.UtcNow;

        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId, TeamId = teamId, WorkflowId = null, SourceType = WorkflowRunSourceTypes.Snapshot,
            ActorType = "user", ActorId = SystemUsers.SeederId, NormalizedPayloadJson = "{}",
            Status = WorkflowRunRequestStatus.Consumed, ReceivedAt = t, VerifiedAt = t, NormalizedAt = t,
        });
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = runId, WorkflowId = null, WorkflowVersion = null, TeamId = teamId, RunRequestId = requestId,
            SourceType = WorkflowRunSourceTypes.Snapshot, Status = WorkflowRunStatus.Suspended,
            ScopeRepositoryIds = [], ScopeProjectIds = [], CreatedDate = t,
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });
        await db.SaveChangesAsync();

        await AppendRecordAsync(runId, WorkflowRunRecordTypes.RunStarted, "{}");
        await AppendRecordAsync(runId, WorkflowRunRecordTypes.Log, $$"""{"level":"info","message":"{{SuspendedRunMarker}}"}""");

        return runId;
    }

    private async Task AppendRecordAsync(Guid runId, string recordType, string payload)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();

        db.WorkflowRunRecord.Add(new WorkflowRunRecord { Id = Guid.NewGuid(), RunId = runId, RecordType = recordType, NodeId = null, OccurredAt = DateTimeOffset.UtcNow, PayloadJson = payload });
        await db.SaveChangesAsync();
    }

    public enum LostStanding { NeverAMember, RemovedFromTeam, TeamDeleted }

    /// <summary>Returns a caller who sends the run's team in <c>X-Team-Id</c> but holds no standing there, after proving the run streams for someone who does.</summary>
    private async Task<Guid> ArrangeCallerWithoutStandingAsync(LostStanding standing, Guid ownerId, Guid teamId, Guid runId)
    {
        switch (standing)
        {
            case LostStanding.NeverAMember:
            {
                await ShouldStreamAsync(ownerId, teamId, runId, "control: the run's own team streams it");

                var (outsiderId, _) = await SeedTeamMembershipAsync();   // Owner of an unrelated team, no row on this one
                return outsiderId;
            }
            case LostStanding.RemovedFromTeam:
            {
                var memberId = await AddMemberAsync(teamId, TeamRole.Member);
                await ShouldStreamAsync(memberId, teamId, runId, "control: the member streams the run while still a member");

                using var removal = await SendAsync(HttpMethod.Delete, $"/api/teams/members/{memberId}", ownerId, teamId);
                removal.StatusCode.ShouldBe(HttpStatusCode.NoContent, customMessage: "the owner removes the member through the real endpoint: " + await removal.Content.ReadAsStringAsync());

                return memberId;
            }
            case LostStanding.TeamDeleted:
            {
                await ShouldStreamAsync(ownerId, teamId, runId, "control: the owner streams the run before the team is deleted");

                await SoftDeleteTeamAsync(teamId);
                return ownerId;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(standing), standing, null);
        }
    }

    private async Task ShouldStreamAsync(Guid userId, Guid teamId, Guid runId, string because)
    {
        using var response = await OpenStreamAsync(userId, teamId, runId);
        var body = await response.Content.ReadAsStringAsync();   // the seeded run is terminal, so the stream closes itself

        response.StatusCode.ShouldBe(HttpStatusCode.OK, customMessage: $"{because}; body: {body}");
        body.ShouldContain("event: " + WorkflowRunRecordTypes.RunFailed, customMessage: because);
    }

    private Task<HttpResponseMessage> OpenStreamAsync(Guid userId, Guid teamId, Guid runId) => OpenStreamAsync(MintToken(userId), teamId, runId);

    private async Task<HttpResponseMessage> OpenStreamAsync(string token, Guid teamId, Guid runId)
    {
        var client = _factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);   // safety net — a refused or terminal stream closes on its own

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/workflows/runs/{runId}/records/stream?after=0");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Team-Id", teamId.ToString());

        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    }

    private async Task<Guid> AddMemberAsync(Guid teamId, TeamRole role)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var passwordHash = scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(MemberPassword);
        var userId = Guid.NewGuid();

        db.User.Add(new User { SecurityStamp = TestToken.SeedStamp, PasswordHash = passwordHash, Id = userId, Email = $"e2e-member-{userId:N}@test.local", Name = "E2E member", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = role, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });

        await db.SaveChangesAsync();
        return userId;
    }

    private async Task SoftDeleteTeamAsync(Guid teamId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();

        await db.Team.Where(t => t.Id == teamId).ExecuteUpdateAsync(s => s.SetProperty(t => t.DeletedDate, DateTimeOffset.UtcNow));
    }

    private async Task<Guid> SeedRunWithRecordsAsync(Guid teamId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();

        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var t = DateTimeOffset.UtcNow;

        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId, TeamId = teamId, WorkflowId = null, SourceType = WorkflowRunSourceTypes.Snapshot,
            ActorType = "user", ActorId = SystemUsers.SeederId, NormalizedPayloadJson = "{}",
            Status = WorkflowRunRequestStatus.Consumed, ReceivedAt = t, VerifiedAt = t, NormalizedAt = t,
        });
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = runId, WorkflowId = null, WorkflowVersion = null, TeamId = teamId, RunRequestId = requestId,
            SourceType = WorkflowRunSourceTypes.Snapshot, Status = WorkflowRunStatus.Failure,
            ScopeRepositoryIds = [], ScopeProjectIds = [], CreatedDate = t,
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();   // persist the run + request first

        var records = new (string Type, string? NodeId, string Payload)[]
        {
            (WorkflowRunRecordTypes.RunStarted, null, "{}"),
            (WorkflowRunRecordTypes.ScopeResolved, null, """{"repos":1}"""),
            (WorkflowRunRecordTypes.Log, "code", """{"level":"info","message":"noise"}"""),
            (WorkflowRunRecordTypes.NodeStarted, "code", "{}"),
            (WorkflowRunRecordTypes.RunFailed, null, """{"error":"boom"}"""),
        };

        for (var i = 0; i < records.Length; i++)
        {
            var (type, nodeId, payload) = records[i];
            db.WorkflowRunRecord.Add(new WorkflowRunRecord { Id = Guid.NewGuid(), RunId = runId, RecordType = type, NodeId = nodeId, OccurredAt = t.AddSeconds(i), PayloadJson = payload });

            // Save per row so the BIGSERIAL Sequence increments in add-order (a batched save does not guarantee it).
            await db.SaveChangesAsync();
        }

        return runId;
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, Guid userId, Guid teamId) => SendAsync(method, url, MintToken(userId), teamId);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string token, Guid teamId)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Team-Id", teamId.ToString());

        return await _factory.CreateClient().SendAsync(request);
    }

    private async Task<(Guid UserId, Guid TeamId)> SeedTeamMembershipAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();

        db.User.Add(new User {
            SecurityStamp = TestToken.SeedStamp, Id = userId, Email = $"e2e-{suffix}@test.local", Name = "E2E", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Team.Add(new Team { Id = teamId, Slug = $"e2e-{suffix}", Name = "E2E", Kind = TeamKind.Workspace, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });

        await db.SaveChangesAsync();
        return (userId, teamId);
    }

    /// <summary>Delegates to the shared helper so the security-stamp claim cannot be forgotten here.</summary>
    private static string MintToken(Guid userId) => TestToken.Mint(userId, TestToken.SeedStamp);

    private static async Task<string> DescribeFailureAsync(HttpResponseMessage response) =>
        $"expected 200 but got {(int)response.StatusCode}; body: {await response.Content.ReadAsStringAsync()}";
}
