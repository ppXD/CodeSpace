using System.Net;
using System.Text.Json;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.E2ETests.Chat;

/// <summary>
/// 🟢 High fidelity (real HTTP pipeline, Postgres): a conversation in a team can only ever hold people in that team.
///
/// <para>The gap this pins: opening a direct message, creating a group and adding a member each wrote whatever user
/// id the caller sent. A user of another team, or an id that named nobody, was accepted; and the conversation list
/// then counted the rows whose user existed, so <c>memberCount</c> and <c>memberUserIds</c> told the caller whether
/// an arbitrary user id was a real account.</para>
///
/// <para>An id outside the team is refused before anything is written, with the same answer whether it belongs to
/// another team or to nobody — telling those two apart is the oracle.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class ConversationParticipantTenancyE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private readonly TenantHarness _tenants;

    public ConversationParticipantTenancyE2ETests(TaskLaunchApiFactory factory) { _tenants = new TenantHarness(factory); }

    [Fact]
    public async Task A_direct_message_to_someone_outside_the_team_is_refused_the_same_way_as_one_to_nobody()
    {
        var world = await SeedWorldAsync();

        var foreign = await _tenants.SendAsync(HttpMethod.Post, "/api/conversations/direct", world.Caller, world.TeamId, new { otherUserId = world.Foreigner });
        var unknown = await _tenants.SendAsync(HttpMethod.Post, "/api/conversations/direct", world.Caller, world.TeamId, new { otherUserId = Guid.NewGuid() });

        await ShouldBeTheSameRefusalAsync(foreign, unknown);
        (await ConversationCountAsync(world.TeamId)).ShouldBe(0, "a refused open writes no conversation");
    }

    [Fact]
    public async Task A_group_naming_someone_outside_the_team_is_refused_the_same_way_as_one_naming_nobody()
    {
        var world = await SeedWorldAsync();

        var foreign = await _tenants.SendAsync(HttpMethod.Post, "/api/conversations/groups", world.Caller, world.TeamId, new { name = "g", memberUserIds = new[] { world.Teammate, world.Foreigner } });
        var unknown = await _tenants.SendAsync(HttpMethod.Post, "/api/conversations/groups", world.Caller, world.TeamId, new { name = "g", memberUserIds = new[] { world.Teammate, Guid.NewGuid() } });

        await ShouldBeTheSameRefusalAsync(foreign, unknown);
        (await ConversationCountAsync(world.TeamId)).ShouldBe(0, "a refused group writes no conversation and no member rows");
    }

    [Fact]
    public async Task Adding_someone_outside_the_team_is_refused_the_same_way_as_adding_nobody()
    {
        var world = await SeedWorldAsync();
        var group = await _tenants.SendAsync(HttpMethod.Post, "/api/conversations/groups", world.Caller, world.TeamId, new { name = "g", memberUserIds = new[] { world.Teammate } });
        group.IsSuccessStatusCode.ShouldBeTrue(await TenantHarness.DescribeAsync(group));
        var groupId = JsonDocument.Parse(await group.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        var foreign = await _tenants.SendAsync(HttpMethod.Post, $"/api/conversations/{groupId}/members", world.Caller, world.TeamId, new { userId = world.Foreigner });
        var unknown = await _tenants.SendAsync(HttpMethod.Post, $"/api/conversations/{groupId}/members", world.Caller, world.TeamId, new { userId = Guid.NewGuid() });

        await ShouldBeTheSameRefusalAsync(foreign, unknown);

        var members = await _tenants.DbAsync(db => db.ConversationMember.AsNoTracking().Where(m => m.ConversationId == groupId).Select(m => m.UserId).ToListAsync());
        members.ShouldBe(new[] { world.Caller, world.Teammate }, ignoreOrder: true, "only the two teammates are in the group");
    }

    [Fact]
    public async Task Teammates_still_open_direct_messages_create_groups_and_add_each_other()
    {
        var world = await SeedWorldAsync();
        var third = await _tenants.AccountAsync();
        await _tenants.AddMemberAsync(world.TeamId, third, TeamRole.Viewer);

        var direct = await _tenants.SendAsync(HttpMethod.Post, "/api/conversations/direct", world.Caller, world.TeamId, new { otherUserId = world.Teammate });
        direct.IsSuccessStatusCode.ShouldBeTrue(await TenantHarness.DescribeAsync(direct));

        var group = await _tenants.SendAsync(HttpMethod.Post, "/api/conversations/groups", world.Caller, world.TeamId, new { name = "g", memberUserIds = new[] { world.Teammate } });
        group.IsSuccessStatusCode.ShouldBeTrue(await TenantHarness.DescribeAsync(group));
        var groupId = JsonDocument.Parse(await group.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        var add = await _tenants.SendAsync(HttpMethod.Post, $"/api/conversations/{groupId}/members", world.Caller, world.TeamId, new { userId = third });
        add.IsSuccessStatusCode.ShouldBeTrue(await TenantHarness.DescribeAsync(add));
    }

    // ─── Plumbing ────────────────────────────────────────────────────────────────

    private sealed record World(Guid TeamId, Guid Caller, Guid Teammate, Guid Foreigner);

    private async Task<World> SeedWorldAsync()
    {
        var caller = await _tenants.AccountAsync();
        var teammate = await _tenants.AccountAsync();
        var foreigner = await _tenants.AccountAsync();
        var teamId = await _tenants.TeamAsync((caller, TeamRole.Member), (teammate, TeamRole.Member));
        await _tenants.TeamAsync((foreigner, TeamRole.Member));

        return new World(teamId, caller, teammate, foreigner);
    }

    private static async Task ShouldBeTheSameRefusalAsync(HttpResponseMessage foreign, HttpResponseMessage unknown)
    {
        foreign.StatusCode.ShouldBe(HttpStatusCode.NotFound, $"a user of another team — {await TenantHarness.DescribeAsync(foreign)}");
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound, $"an id that names nobody — {await TenantHarness.DescribeAsync(unknown)}");

        var foreignBody = await foreign.Content.ReadAsStringAsync();
        var unknownBody = await unknown.Content.ReadAsStringAsync();

        JsonDocument.Parse(foreignBody).RootElement.GetProperty("code").GetString().ShouldBe("conversation_participant_not_in_team");
        foreignBody.ShouldBe(unknownBody, "another team's user and a user who does not exist must be indistinguishable");
    }

    private async Task<int> ConversationCountAsync(Guid teamId) =>
        await _tenants.DbAsync(db => db.Conversation.AsNoTracking().CountAsync(c => c.TeamId == teamId));
}
