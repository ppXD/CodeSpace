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
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeSpace.E2ETests.Agents;

/// <summary>
/// 🟢 High fidelity (real HTTP/auth/EF/Postgres): Q4's claim board through the wire — a member reads the grid,
/// a seeded sealed receipt shows Sealed ONLY on its own pair (with the receipt's identity on the row), an
/// unminted pair reads Unmeasured, and an anonymous caller is refused.
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

        var unminted = rows.Single(r => r.GetProperty("mode").GetString() == RunModeKeys.PlanMap && r.GetProperty("capabilityKey").GetString() == CapabilityKeys.InlineAnswer);
        unminted.GetProperty("performance").GetString().ShouldBe("Unmeasured", "no receipt was ever minted for this pair — the board must say so, never inherit a neighbour's standing");
        unminted.GetProperty("receiptId").ValueKind.ShouldBe(JsonValueKind.Null);
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

    private async Task<World> SeedWorldAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, SecurityStamp = TestToken.SeedStamp, Email = $"claims-{suffix}@test.local", Name = "Claims Reader", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Team.Add(new Team { Id = teamId, Slug = $"claims-{suffix}", Name = "Claims", Kind = TeamKind.Workspace, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Member, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
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
            GrantedPerformance = PerformanceQualification.Sealed,
            EffectiveFrom = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        await scope.ServiceProvider.GetRequiredService<IQualificationReceiptStore>().AppendAsync(receipt, CancellationToken.None);
        return receipt.Id;
    }

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
