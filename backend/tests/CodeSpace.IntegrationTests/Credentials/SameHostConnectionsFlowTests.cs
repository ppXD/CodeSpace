using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.OAuth;
using CodeSpace.Messages.Commands.Credentials;
using CodeSpace.Messages.Commands.Repositories;
using CodeSpace.Messages.Dtos.Credentials;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Credentials;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Credentials;

/// <summary>
/// A team that signs in to one GitLab through two OAuth applications holds two provider connections to the same host. A repository
/// bound under one of them, through a credential that died, could be taken over only by a credential of that same connection: the
/// Re-link dialog offered nothing, the relink commands refused, and a reconnect through the other connection carried nothing
/// forward — so a person who had just connected GitLab still saw "No other working credential".
///
/// <para>These are the doors, driven through the real mediator pipeline against real Postgres, with one rule behind every one:
/// connections to the same host (same team, provider and API root) are interchangeable for a repository, and what a credential
/// of one takes over from the other keeps its identity — the repository's id, its project links, its history — and moves
/// the repository to the new connection with it. This part is the owner's report as it first reproduced: each door, asked to cross
/// from one connection to its sibling. The hooks a move re-registers, the probe that guards it, the refusals and the
/// candidate list are the other parts.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed partial class SameHostConnectionsFlowTests
{
    private readonly BindingMoveKit _kit;

    public SameHostConnectionsFlowTests(PostgresFixture fixture) { _kit = new BindingMoveKit(fixture); }

    [Fact]
    public async Task A_repository_is_relinked_to_the_callers_live_credential_on_a_sibling_connection()
    {
        var world = await _kit.SeedWorldAsync();
        var sibling = await _kit.SeedSiblingInstanceAsync(world);
        var dead = await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-dead", CredentialStatus.Revoked);
        var repoId = await _kit.SeedRepositoryAsync(world, dead, RepositoryStatus.Error);
        var target = await _kit.SeedCredentialAsync(world, world.AdminId, "glpat-target", instanceId: sibling);

        await RelinkOneAsync(world, world.AdminId, repoId, target);

        var repo = await _kit.LoadRepositoryAsync(repoId);

        repo.CredentialId.ShouldBe(target);
        repo.ProviderInstanceId.ShouldBe(sibling, "the repository now lives under the connection its credential belongs to — the two always agree");
        repo.Status.ShouldBe(RepositoryStatus.Active);
        repo.LastError.ShouldBeNull();
        (await _kit.ResolveCloneTokenAsync(world, repoId)).ShouldBe("glpat-target", "the next run clones through the credential that took over");
    }

    [Fact]
    public async Task Relinking_everything_a_dead_credential_held_reaches_a_sibling_connection()
    {
        var world = await _kit.SeedWorldAsync();
        var sibling = await _kit.SeedSiblingInstanceAsync(world);
        var dead = await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-dead", CredentialStatus.Revoked);
        var first = await _kit.SeedRepositoryAsync(world, dead, RepositoryStatus.Error);
        var second = await _kit.SeedRepositoryAsync(world, dead, RepositoryStatus.Error);
        var target = await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-target", instanceId: sibling);

        var result = await RelinkAllAsync(world, world.OwnerId, dead, target);

        result.MovedRepositoryCount.ShouldBe(2);

        foreach (var repoId in new[] { first, second })
        {
            var repo = await _kit.LoadRepositoryAsync(repoId);

            repo.CredentialId.ShouldBe(target);
            repo.ProviderInstanceId.ShouldBe(sibling);
            repo.Status.ShouldBe(RepositoryStatus.Active);
        }
    }

    [Fact]
    public async Task Suggestions_for_a_dead_credential_offer_a_sibling_connections_credential_after_the_same_connections()
    {
        var world = await _kit.SeedWorldAsync();
        var sibling = await _kit.SeedSiblingInstanceAsync(world);
        var dead = await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-dead", CredentialStatus.Revoked);
        var onSibling = await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-sibling", instanceId: sibling, createdDate: DateTimeOffset.UtcNow);
        var onSameConnection = await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-same", createdDate: DateTimeOffset.UtcNow.AddDays(-5));

        var suggestions = await SuggestAsync(world, world.OwnerId, dead);

        suggestions.Select(s => s.CredentialId).ShouldBe(new[] { onSameConnection, onSibling }, "the same connection's first, though the sibling's was connected later");
        suggestions.Single(s => s.CredentialId == onSibling).ProviderInstanceId.ShouldBe(sibling);
    }

    [Fact]
    public async Task Connecting_on_a_sibling_connection_carries_the_owners_stranded_repositories_forward()
    {
        var world = await _kit.SeedWorldAsync();
        var sibling = await _kit.SeedSiblingInstanceAsync(world);
        var dead = await _kit.SeedCredentialAsync(world, world.AdminId, "glpat-dead", CredentialStatus.Revoked, createdDate: DateTimeOffset.UtcNow.AddDays(-10));
        var repoId = await _kit.SeedRepositoryAsync(world, dead, RepositoryStatus.Error);

        var connected = await _kit.AddTokenAsync(world, world.AdminId, "glpat-new", instanceId: sibling);

        var repo = await _kit.LoadRepositoryAsync(repoId);

        repo.CredentialId.ShouldBe(connected, "what the owner bound follows them to the credential they connected after");
        repo.ProviderInstanceId.ShouldBe(sibling);
        repo.Status.ShouldBe(RepositoryStatus.Active);
    }

    [Fact]
    public async Task The_repair_sweep_follows_the_owner_to_a_sibling_connection()
    {
        var world = await _kit.SeedWorldAsync();
        var sibling = await _kit.SeedSiblingInstanceAsync(world);
        var revoked = DateTimeOffset.UtcNow.AddDays(-10);
        var dead = await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-dead", CredentialStatus.Revoked, createdDate: revoked);
        var successor = await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-successor", instanceId: sibling, createdDate: revoked.AddDays(1));
        var repoId = await _kit.SeedRepositoryAsync(world, dead, RepositoryStatus.Error);

        await SweepUntilAsync(async () => (await _kit.LoadRepositoryAsync(repoId)).CredentialId == successor);

        var repo = await _kit.LoadRepositoryAsync(repoId);

        repo.CredentialId.ShouldBe(successor);
        repo.ProviderInstanceId.ShouldBe(sibling);
        repo.Status.ShouldBe(RepositoryStatus.Active);
    }

    // ── Doors ─────────────────────────────────────────────────────────────────────

    private async Task RelinkOneAsync(BindingMoveKit.World world, Guid userId, Guid repositoryId, Guid to, CredentialRelinkFlowTests.ProbeRecorder? provider = null)
    {
        using var scope = _kit.Scope(world, userId, provider == null ? null : provider.Install);

        await scope.Resolve<IMediator>().Send(new RelinkRepositoryCredentialCommand { RepositoryId = repositoryId, NewCredentialId = to });
    }

    private async Task<RelinkCredentialBindingsResult> RelinkAllAsync(BindingMoveKit.World world, Guid userId, Guid from, Guid to, CredentialRelinkFlowTests.ProbeRecorder? provider = null)
    {
        using var scope = _kit.Scope(world, userId, provider == null ? null : provider.Install);

        return await scope.Resolve<IMediator>().Send(new RelinkCredentialBindingsCommand { FromCredentialId = from, ToCredentialId = to });
    }

    private async Task<IReadOnlyList<RelinkSuggestion>> SuggestAsync(BindingMoveKit.World world, Guid userId, Guid credentialId)
    {
        using var scope = _kit.Scope(world, userId);

        return await scope.Resolve<IMediator>().Send(new GetRelinkSuggestionsQuery { CredentialId = credentialId });
    }

    /// <summary>
    /// Sweeps until the row the test owns has moved. The repair is deployment-wide, so what one pass repairs says nothing about this
    /// test's row: a test waits on the row it owns, never on a tally of the pass.
    /// </summary>
    private async Task SweepUntilAsync(Func<Task<bool>> ownedRowHasMoved)
    {
        for (var pass = 1; pass <= MaxSweeps; pass++)
        {
            using (var scope = _kit.Fixture.BeginScope()) await scope.Resolve<IMediator>().Send(new RepairDisconnectedCredentialBindingsCommand { BatchSize = 50 });

            if (await ownedRowHasMoved()) return;
        }
    }

    private const int MaxSweeps = 20;
}
