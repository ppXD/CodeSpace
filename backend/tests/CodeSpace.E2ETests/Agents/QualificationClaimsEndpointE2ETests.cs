using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Completion;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeSpace.E2ETests.Agents;

/// <summary>
/// 🟢 High fidelity (real HTTP/auth/EF/Postgres): Q4's claim board through the wire — a member reads the grid,
/// a seeded sealed receipt shows Sealed ONLY on its own pair (with the receipt's identity on the row), an
/// unminted pair reads Unmeasured, and an anonymous caller is refused.
///
/// <para>The board is platform-wide, but the receipt behind a row was paid for by one team: its cohort names that
/// team and its verifier bundle names that team's model credential. Those identifiers reach only a reader in the
/// paying team or a global admin; everyone else reads the same standing without them. Each test below seeds its own
/// (mode × capability) pair, because the board shows one receipt per pair and a shared pair would let tests overwrite
/// each other's rows.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class QualificationClaimsEndpointE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private readonly TaskLaunchApiFactory _factory;

    public QualificationClaimsEndpointE2ETests(TaskLaunchApiFactory factory) { _factory = factory; }

    [Fact]
    public async Task A_member_reads_the_board_and_a_sealed_receipt_shows_only_on_its_pair()
    {
        var world = await SeedWorldAsync();
        var digest = "sha256:e2e-" + Guid.NewGuid().ToString("N")[..8];
        var receiptId = await SeedSealedReceiptAsync(RunModeKeys.Supervisor, CapabilityKeys.GitBranch, digest);

        var response = await SendAsync(world.UserId, world.TeamId, "/api/agents/qualification-claims");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await DescribeAsync(response));

        var rows = (await JsonAsync(response)).GetProperty("rows").EnumerateArray().ToList();

        var sealedRow = rows.Single(r => r.GetProperty("mode").GetString() == RunModeKeys.Supervisor && r.GetProperty("capabilityKey").GetString() == CapabilityKeys.GitBranch);
        sealedRow.GetProperty("performance").GetString().ShouldBe("Sealed", "a current sealed receipt exists for exactly this pair");
        sealedRow.GetProperty("receiptId").GetGuid().ShouldBe(receiptId, "the wire row must carry the round that earned the standing");
        sealedRow.GetProperty("suiteDigest").GetString().ShouldBe(digest);
        sealedRow.GetProperty("seal").GetProperty("verifierBundle").GetProperty("harness").GetString().ShouldBe("codex-cli", "the wire says WHO earned the standing — the audit trail reaches the UI");
        sealedRow.GetProperty("cohort").GetProperty("tier").GetString().ShouldBe("internal-qualification");

        var unminted = rows.Single(r => r.GetProperty("mode").GetString() == RunModeKeys.PlanMap && r.GetProperty("capabilityKey").GetString() == CapabilityKeys.InlineAnswer);
        unminted.GetProperty("performance").GetString().ShouldBe("Unmeasured", "no receipt was ever minted for this pair — the board must say so, never inherit a neighbour's standing");
        unminted.GetProperty("receiptId").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_reader_outside_the_paying_team_sees_the_standing_without_the_teams_identifiers()
    {
        var payer = await SeedWorldAsync();
        var outsider = await SeedWorldAsync(TeamRole.Viewer);
        var receipt = await SeedPaidReceiptAsync(RunModeKeys.SingleAgent, CapabilityKeys.GitBranch, payer.TeamId);

        var response = await SendAsync(outsider.UserId, outsider.TeamId, "/api/agents/qualification-claims");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await DescribeAsync(response));
        var body = await response.Content.ReadAsStringAsync();

        body.ShouldNotContain(payer.TeamId.ToString(), Case.Insensitive, "the paying team's id is not the outsider's to learn");
        body.ShouldNotContain(receipt.ModelCredentialId.ToString(), Case.Insensitive, "nor is its model credential's id");
        body.ShouldNotContain(receipt.ModelCredentialModelId.ToString(), Case.Insensitive, "nor its credentialed model row's id");

        var row = RowFor(JsonDocument.Parse(body).RootElement, RunModeKeys.SingleAgent, CapabilityKeys.GitBranch);
        row.GetProperty("performance").GetString().ShouldBe("Sealed", "the standing itself is platform-wide and still reads");
        row.GetProperty("receiptId").GetGuid().ShouldBe(receipt.ReceiptId);
        row.GetProperty("cohort").GetProperty("tier").GetString().ShouldBe("internal-qualification");
        row.GetProperty("cohort").GetProperty("teamId").ValueKind.ShouldBe(JsonValueKind.Null);
        row.GetProperty("seal").GetProperty("verifierBundle").GetProperty("harness").GetString().ShouldBe("codex-cli");
        row.GetProperty("seal").GetProperty("verifierBundle").GetProperty("modelCredentialId").ValueKind.ShouldBe(JsonValueKind.Null);
        row.GetProperty("seal").GetProperty("verifierBundle").GetProperty("modelCredentialModelId").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_member_of_the_paying_team_sees_the_teams_identifiers()
    {
        var payer = await SeedWorldAsync(TeamRole.Viewer);
        var receipt = await SeedPaidReceiptAsync(RunModeKeys.SingleAgent, CapabilityKeys.GitPatch, payer.TeamId);

        var row = await ReadRowAsync(payer, RunModeKeys.SingleAgent, CapabilityKeys.GitPatch);

        row.GetProperty("cohort").GetProperty("teamId").GetGuid().ShouldBe(payer.TeamId);
        row.GetProperty("seal").GetProperty("verifierBundle").GetProperty("modelCredentialId").GetGuid().ShouldBe(receipt.ModelCredentialId);
        row.GetProperty("seal").GetProperty("verifierBundle").GetProperty("modelCredentialModelId").GetGuid().ShouldBe(receipt.ModelCredentialModelId);
    }

    [Fact]
    public async Task A_global_admin_sees_the_paying_teams_identifiers_from_any_team()
    {
        var payer = await SeedWorldAsync();
        var admin = await SeedWorldAsync();
        await GrantGlobalAdminAsync(admin.UserId);
        var receipt = await SeedPaidReceiptAsync(RunModeKeys.Supervisor, CapabilityKeys.GitPatch, payer.TeamId);

        var row = await ReadRowAsync(admin, RunModeKeys.Supervisor, CapabilityKeys.GitPatch);

        row.GetProperty("cohort").GetProperty("teamId").GetGuid().ShouldBe(payer.TeamId, "the operator who minted the round reads it whole");
        row.GetProperty("seal").GetProperty("verifierBundle").GetProperty("modelCredentialId").GetGuid().ShouldBe(receipt.ModelCredentialId);
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused()
    {
        var world = await SeedWorldAsync();

        var response = await SendAsync(userId: null, world.TeamId, "/api/agents/qualification-claims");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // ─── Plumbing ────────────────────────────────────────────────────────────────

    private sealed record World(Guid UserId, Guid TeamId);

    private sealed record PaidReceipt(Guid ReceiptId, Guid ModelCredentialId, Guid ModelCredentialModelId);

    private async Task<World> SeedWorldAsync(TeamRole role = TeamRole.Member)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, SecurityStamp = TestToken.SeedStamp, Email = $"claims-{suffix}@test.local", Name = $"Claims Reader {suffix}", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Team.Add(new Team { Id = teamId, Slug = $"claims-{suffix}", Name = "Claims", Kind = TeamKind.Workspace, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = role, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        await db.SaveChangesAsync();
        return new World(userId, teamId);
    }

    private async Task<Guid> SeedSealedReceiptAsync(string mode, string capabilityKey, string digest)
    {
        using var scope = _factory.Services.CreateScope();
        var receipt = new QualificationReceipt
        {
            Id = Guid.NewGuid(),
            Mode = mode,
            CapabilityKey = capabilityKey,
            SuiteDigest = digest,
            VerifierBundleJson = """{"harness":"codex-cli","model":"test-model","modelCredentialId":null}""",
            CohortJson = $$"""{"teamId":"{{Guid.NewGuid()}}","mode":"{{mode}}","tier":"internal-qualification","completionPolicyVersion":2}""",
            GrantedPerformance = PerformanceQualification.Sealed,
            EffectiveFrom = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        await scope.ServiceProvider.GetRequiredService<IQualificationReceiptStore>().AppendAsync(receipt, CancellationToken.None);
        return receipt.Id;
    }

    /// <summary>A sealed receipt minted the way QualificationRunner mints one: the cohort names the team whose X-Team-Id paid for the round, and the verifier bundle names that team's model credential.</summary>
    private async Task<PaidReceipt> SeedPaidReceiptAsync(string mode, string capabilityKey, Guid payingTeamId)
    {
        using var scope = _factory.Services.CreateScope();
        var paid = new PaidReceipt(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var receipt = new QualificationReceipt
        {
            Id = paid.ReceiptId,
            Mode = mode,
            CapabilityKey = capabilityKey,
            SuiteDigest = "sha256:paid-" + Guid.NewGuid().ToString("N")[..8],
            VerifierBundleJson = $$"""{"harness":"codex-cli","model":"team-private-model","modelCredentialId":"{{paid.ModelCredentialId}}","modelCredentialModelId":"{{paid.ModelCredentialModelId}}"}""",
            CohortJson = $$"""{"teamId":"{{payingTeamId}}","mode":"{{mode}}","tier":"internal-qualification","completionPolicyVersion":2}""",
            GrantedPerformance = PerformanceQualification.Sealed,
            EffectiveFrom = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        await scope.ServiceProvider.GetRequiredService<IQualificationReceiptStore>().AppendAsync(receipt, CancellationToken.None);
        return paid;
    }

    private async Task GrantGlobalAdminAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var roleId = await db.Role.Where(r => r.Name == Roles.Admin).Select(r => r.Id).SingleAsync();
        db.RoleUser.Add(new RoleUser { Id = Guid.NewGuid(), RoleId = roleId, UserId = userId });
        await db.SaveChangesAsync();
    }

    private async Task<JsonElement> ReadRowAsync(World reader, string mode, string capabilityKey)
    {
        var response = await SendAsync(reader.UserId, reader.TeamId, "/api/agents/qualification-claims");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await DescribeAsync(response));

        return RowFor(await JsonAsync(response), mode, capabilityKey);
    }

    private static JsonElement RowFor(JsonElement board, string mode, string capabilityKey) =>
        board.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("mode").GetString() == mode && r.GetProperty("capabilityKey").GetString() == capabilityKey).Clone();

    private async Task<HttpResponseMessage> SendAsync(Guid? userId, Guid teamId, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (userId is { } id) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestToken.Mint(id, TestToken.SeedStamp));
        request.Headers.Add("X-Team-Id", teamId.ToString());
        return await _factory.CreateClient().SendAsync(request);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    private static async Task<string> DescribeAsync(HttpResponseMessage response) => $"got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}";
}
