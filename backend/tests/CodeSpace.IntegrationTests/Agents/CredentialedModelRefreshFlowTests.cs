using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Identity;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.ModelCredentials;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// The reflection UPSERT against real Postgres, driven through MediatR with a FAKE reflector (so the team-membership
/// behaviour + handler + <c>ModelCredentialService</c> all run, without a live gateway). Pins the reconciliation
/// contract: a reflected model is added by id; a MANUAL row is never clobbered; a vanished reflected
/// row is disabled (not deleted) and re-enabled when it reappears; a non-reflectable credential is a no-op; and the
/// refresh is idempotent.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class CredentialedModelRefreshFlowTests
{
    private readonly PostgresFixture _fixture;

    public CredentialedModelRefreshFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Refresh_adds_reflected_models_by_id()
    {
        var (userId, teamId) = await SeedTeamAsync();
        var credId = await AddCredentialAsync(userId, teamId);

        var count = await RefreshAsync(userId, teamId, credId, Reflectable(
            Model("claude-opus-4-8"),
            Model("custom/x")));

        count.ShouldBe(2);

        var rows = await RowsAsync(credId);
        rows.Count.ShouldBe(2);

        var opus = rows.Single(r => r.ModelId == "claude-opus-4-8");
        opus.Source.ShouldBe(ModelSource.Reflected);
        opus.Enabled.ShouldBeTrue();
    }

    [Fact]
    public async Task Refresh_never_clobbers_a_manual_row()
    {
        var (userId, teamId) = await SeedTeamAsync();
        var credId = await AddCredentialAsync(userId, teamId);
        await SendAsync(userId, teamId, new AddCredentialedModelCommand { ModelCredentialId = credId, ModelId = "gpt-5.4" });

        // The gateway reflects the SAME id — the operator's manual row must stay sovereign.
        await RefreshAsync(userId, teamId, credId, Reflectable(Model("gpt-5.4")));

        var row = (await RowsAsync(credId)).ShouldHaveSingleItem();
        row.Source.ShouldBe(ModelSource.Manual, "a reflected id matching a manual row leaves the manual row untouched");
    }

    [Fact]
    public async Task A_vanished_reflected_model_is_disabled_then_re_enabled_when_it_reappears()
    {
        var (userId, teamId) = await SeedTeamAsync();
        var credId = await AddCredentialAsync(userId, teamId);

        await RefreshAsync(userId, teamId, credId, Reflectable(Model("a"), Model("b")));
        await RefreshAsync(userId, teamId, credId, Reflectable(Model("a")));   // b vanished

        var afterVanish = await RowsAsync(credId);
        afterVanish.Single(r => r.ModelId == "a").Enabled.ShouldBeTrue();
        var b = afterVanish.Single(r => r.ModelId == "b");
        b.Enabled.ShouldBeFalse("a vanished reflected model is disabled, not deleted (provenance kept)");

        await RefreshAsync(userId, teamId, credId, Reflectable(Model("a"), Model("b")));   // b reappears

        (await RowsAsync(credId)).Single(r => r.ModelId == "b").Enabled.ShouldBeTrue("a reappeared model is re-enabled");
    }

    [Fact]
    public async Task A_non_reflectable_credential_is_a_no_op()
    {
        var (userId, teamId) = await SeedTeamAsync();
        var credId = await AddCredentialAsync(userId, teamId);

        var count = await RefreshAsync(userId, teamId, credId, new FakeReflector(canReflect: false, Array.Empty<ReflectedModel>()));

        count.ShouldBe(0, "a manual-only credential reflects nothing");
        (await RowsAsync(credId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Refresh_is_idempotent()
    {
        var (userId, teamId) = await SeedTeamAsync();
        var credId = await AddCredentialAsync(userId, teamId);

        await RefreshAsync(userId, teamId, credId, Reflectable(Model("a"), Model("b")));
        await RefreshAsync(userId, teamId, credId, Reflectable(Model("a"), Model("b")));

        (await RowsAsync(credId)).Count.ShouldBe(2, "re-reflecting the same set adds no duplicates");
    }

    [Fact]
    public async Task Concurrent_refreshes_do_not_throw_and_converge_to_no_duplicates()
    {
        var (userId, teamId) = await SeedTeamAsync();
        var credId = await AddCredentialAsync(userId, teamId);

        var models = new[] { Model("a"), Model("b"), Model("c") };

        // Several refreshes contend for the same credential — the per-credential advisory lock serializes them, so
        // none races the (credential, model id) unique index into a 23505 (no unhandled 500 out of the thin handler)
        // and the list converges to exactly the reflected set.
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => RefreshAsync(userId, teamId, credId, new FakeReflector(true, models))));

        // No duplicates after a concurrent refresh — the list converges to exactly the reflected set.
        var ids = (await RowsAsync(credId)).Select(r => r.ModelId).OrderBy(x => x).ToList();
        ids.ShouldBe(new[] { "a", "b", "c" });
    }

    // ─── Helpers ───

    private static ReflectedModel Model(string id) => new() { ModelId = id };

    private static FakeReflector Reflectable(params ReflectedModel[] models) => new(canReflect: true, models);

    private async Task<int> RefreshAsync(Guid userId, Guid teamId, Guid credId, IModelReflector reflector)
    {
        using var scope = _fixture.BeginScope(b =>
        {
            b.RegisterInstance(new TestCurrentUser(userId, "test", Roles.Admin)).As<ICurrentUser>().SingleInstance();
            b.RegisterInstance(new TestCurrentTeam(teamId)).As<ICurrentTeam>().SingleInstance();
            b.RegisterInstance(reflector).As<IModelReflector>().SingleInstance();
        });
        return await scope.Resolve<IMediator>().Send(new RefreshCredentialedModelsCommand { ModelCredentialId = credId });
    }

    private async Task<List<ModelCredentialModel>> RowsAsync(Guid credId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().ModelCredentialModel.AsNoTracking()
            .Where(m => m.ModelCredentialId == credId).ToListAsync();
    }

    private async Task<Guid> AddCredentialAsync(Guid userId, Guid teamId) =>
        await SendAsync(userId, teamId, new AddModelCredentialCommand { Provider = "Custom", DisplayName = "Gateway", ApiKey = "sk-x", BaseUrl = "https://gateway.local" });

    private async Task<TResult> SendAsync<TResult>(Guid userId, Guid teamId, IRequest<TResult> request)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(request);
    }

    private async Task<(Guid UserId, Guid TeamId)> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"cmr-{userId:N}@test.local", Name = $"cmr-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"cmr-{teamId:N}", Name = "Refresh Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return (userId, teamId);
    }

    private sealed class FakeReflector : IModelReflector
    {
        private readonly bool _canReflect;
        private readonly IReadOnlyList<ReflectedModel> _models;
        public FakeReflector(bool canReflect, IReadOnlyList<ReflectedModel> models) { _canReflect = canReflect; _models = models; }
        public bool CanReflect(ResolvedModelCredential credential) => _canReflect;
        public Task<IReadOnlyList<ReflectedModel>> ListModelsAsync(ResolvedModelCredential credential, CancellationToken cancellationToken) => Task.FromResult(_models);
    }
}
