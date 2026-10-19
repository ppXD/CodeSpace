using Autofac;
using CodeSpace.Core.Authorization;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.OAuth;
using CodeSpace.Messages.Commands.Credentials;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Dtos.Credentials;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Credentials;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Credentials;

/// <summary>
/// Clearing away the dead rows a Disconnect leaves behind, through the real mediator pipeline: a person who disconnects and
/// reconnects a few times is left with a pile of Revoked credentials, each with its token blanked and nothing bound through it,
/// that the Providers dialog could only list. Removing one soft-deletes it — the shape a departed member's retired credential has —
/// and is refused, with nothing written, for anything else: a credential still in service, one something still leans on, one
/// a reconnect revived while the removal was running.
///
/// <para>Real Postgres; the production pipeline and services. A dead credential is made the way a person makes one — connected
/// through the door, then disconnected through the door — and what leans on it is seeded as a connect or a bind would write it.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RemoveDisconnectedCredentialFlowTests
{
    private readonly BindingMoveKit _kit;

    public RemoveDisconnectedCredentialFlowTests(PostgresFixture fixture) { _kit = new BindingMoveKit(fixture); }

    public enum Door { MemberToken, AdminOAuth }

    [Theory]
    [InlineData(Door.MemberToken)]
    [InlineData(Door.AdminOAuth)]
    public async Task A_disconnected_credential_with_nothing_on_it_is_removed_and_leaves_the_list(Door door)
    {
        var world = await _kit.SeedWorldAsync();
        var person = door == Door.MemberToken ? world.MemberId : world.AdminId;
        var dead = door == Door.MemberToken ? await DisconnectedTokenAsync(world, person) : await DisconnectedOAuthAsync(world, person);

        (await ListAsync(world, person)).Single(c => c.Id == dead).DependentCount.ShouldBe(0, "fixture check: a Disconnect left nothing leaning on it, so the dialog folds this row away");

        var result = await RemoveAsync(world, person, dead);

        result.CredentialId.ShouldBe(dead);

        var row = await _kit.LoadCredentialAsync(dead);

        row.DeletedDate.ShouldNotBeNull("soft-deleted, as a departed member's retired credential is");
        row.Status.ShouldBe(CredentialStatus.Revoked);
        row.EncryptedPayload.ShouldBe(string.Empty, "there is no token to keep");
        row.LastModifiedBy.ShouldBe(person, "the row says who removed it");
        row.LastModifiedDate.ShouldBeGreaterThanOrEqualTo(row.DeletedDate!.Value.AddSeconds(-1));
        (await ListAsync(world, person)).ShouldNotContain(c => c.Id == dead);
    }

    [Fact]
    public async Task Removing_one_credential_leaves_the_others_as_they_were()
    {
        var world = await _kit.SeedWorldAsync();
        var dead = await DisconnectedTokenAsync(world, world.MemberId);
        var alsoDead = await DisconnectedTokenAsync(world, world.MemberId);
        var live = await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-live");

        await RemoveAsync(world, world.MemberId, dead);

        (await _kit.LoadCredentialAsync(alsoDead)).DeletedDate.ShouldBeNull();
        (await _kit.LoadCredentialAsync(live)).DeletedDate.ShouldBeNull();
        (await ListAsync(world, world.MemberId)).Select(c => c.Id).ShouldBe(new[] { alsoDead, live }, ignoreOrder: true);
    }

    [Theory]
    [InlineData(CredentialStatus.Active)]
    [InlineData(CredentialStatus.Error)]
    public async Task A_credential_still_in_service_is_refused_and_not_touched(CredentialStatus status)
    {
        var world = await _kit.SeedWorldAsync();
        var inService = await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-in-service", status);

        var refusal = await Should.ThrowAsync<InvalidOperationException>(() => RemoveAsync(world, world.MemberId, inService));

        refusal.Message.ShouldContain("still connected");

        var row = await _kit.LoadCredentialAsync(inService);

        row.DeletedDate.ShouldBeNull();
        row.Status.ShouldBe(status);
        row.EncryptedPayload.ShouldNotBeEmpty("a live token is never blanked by a removal");
    }

    public enum Lean { Repository, ConnectionWebhook, Identity }

    [Theory]
    [InlineData(Lean.Repository, "1 repository")]
    [InlineData(Lean.ConnectionWebhook, "1 group hook")]
    [InlineData(Lean.Identity, "1 act-as identity")]
    public async Task A_disconnected_credential_something_still_leans_on_is_refused_names_it_and_is_not_touched(Lean lean, string said)
    {
        var world = await _kit.SeedWorldAsync();
        var dead = await DisconnectedTokenAsync(world, world.MemberId);

        await LeanOnAsync(world, dead, lean);

        var refusal = await Should.ThrowAsync<InvalidOperationException>(() => RemoveAsync(world, world.MemberId, dead));

        refusal.Message.ShouldContain(said);
        refusal.Message.ShouldContain("Move or unlink them first");
        (await _kit.LoadCredentialAsync(dead)).DeletedDate.ShouldBeNull($"{lean}: nothing was removed");
        (await ListAsync(world, world.MemberId)).Single(c => c.Id == dead).DependentCount.ShouldBe(1, $"{lean}: the list would not have folded it away either");
    }

    [Fact]
    public async Task What_is_already_gone_does_not_hold_a_removal_back()
    {
        var world = await _kit.SeedWorldAsync();
        var dead = await DisconnectedTokenAsync(world, world.MemberId);
        var removedRepository = await _kit.SeedRepositoryAsync(world, dead, RepositoryStatus.Error);
        var cancelledHook = await _kit.SeedConnectionWebhookAsync(world, dead, RepositoryWebhookRegistrationStatus.Cancelled);
        var endedIdentity = await SeedIdentityAsync(world, world.MemberId, dead);

        await SoftDeleteRepositoryAsync(removedRepository);
        await SoftDeleteIdentityAsync(endedIdentity);

        (await ListAsync(world, world.MemberId)).Single(c => c.Id == dead).DependentCount.ShouldBe(0, $"fixture check: a removed repository, the cancelled hook {cancelledHook} and an ended identity are not dependents");

        await RemoveAsync(world, world.MemberId, dead);

        (await _kit.LoadCredentialAsync(dead)).DeletedDate.ShouldNotBeNull();
    }

    public enum Who { Member, Viewer, Admin, Owner }

    public enum Whose { OwnPersonal, MembersPersonal, AdminsPersonal, TeamToken }

    [Theory]
    [InlineData(Who.Member, Whose.OwnPersonal, true)]
    [InlineData(Who.Viewer, Whose.OwnPersonal, true)]
    [InlineData(Who.Admin, Whose.OwnPersonal, true)]
    [InlineData(Who.Admin, Whose.MembersPersonal, true)]
    [InlineData(Who.Owner, Whose.MembersPersonal, true)]
    [InlineData(Who.Admin, Whose.TeamToken, true)]
    [InlineData(Who.Owner, Whose.TeamToken, true)]
    [InlineData(Who.Member, Whose.AdminsPersonal, false)]
    [InlineData(Who.Member, Whose.TeamToken, false)]
    [InlineData(Who.Viewer, Whose.MembersPersonal, false)]
    [InlineData(Who.Viewer, Whose.TeamToken, false)]
    public async Task Whoever_may_manage_a_credential_may_remove_it_and_nobody_else(Who who, Whose whose, bool allowed)
    {
        var world = await _kit.SeedWorldAsync();
        var asker = IdOf(world, who);
        var dead = whose switch
        {
            Whose.OwnPersonal => await DisconnectedTokenAsync(world, asker),
            Whose.MembersPersonal => await DisconnectedTokenAsync(world, world.MemberId),
            Whose.AdminsPersonal => await DisconnectedTokenAsync(world, world.AdminId),
            _ => await DisconnectedTeamTokenAsync(world)
        };

        var outcome = await Record.ExceptionAsync(() => RemoveAsync(world, asker, dead));

        if (allowed)
        {
            outcome.ShouldBeNull($"{who} removing {whose}");
            (await _kit.LoadCredentialAsync(dead)).DeletedDate.ShouldNotBeNull();
            return;
        }

        outcome.ShouldBeOfType<TenantAccessDeniedException>($"{who} removing {whose}");
        (await _kit.LoadCredentialAsync(dead)).DeletedDate.ShouldBeNull();
    }

    [Fact]
    public async Task A_credential_of_another_team_is_not_the_callers_to_remove()
    {
        var world = await _kit.SeedWorldAsync();
        var elsewhere = await _kit.SeedWorldAsync();
        var foreign = await DisconnectedTokenAsync(elsewhere, elsewhere.MemberId);

        var outcome = await Record.ExceptionAsync(() => RemoveAsync(world, world.OwnerId, foreign));

        outcome.ShouldBeOfType<TenantAccessDeniedException>("an Owner of this team, naming a credential of another");
        (await _kit.LoadCredentialAsync(foreign)).DeletedDate.ShouldBeNull();
    }

    [Fact]
    public async Task Removing_again_is_refused_because_the_credential_is_gone()
    {
        var world = await _kit.SeedWorldAsync();
        var dead = await DisconnectedTokenAsync(world, world.MemberId);

        await RemoveAsync(world, world.MemberId, dead);

        var outcome = await Record.ExceptionAsync(() => RemoveAsync(world, world.MemberId, dead));

        outcome.ShouldBeOfType<TenantAccessDeniedException>();
    }

    [Fact]
    public async Task A_reconnect_that_lands_between_the_checks_and_the_write_keeps_the_credential_and_is_never_overwritten()
    {
        var world = await _kit.SeedWorldAsync();
        var dead = await DisconnectedTokenAsync(world, world.MemberId);
        var reconnect = new ReconnectOnce(_kit, world, dead);

        var refusal = await Should.ThrowAsync<InvalidOperationException>(() => RemoveAsync(world, world.MemberId, dead, reconnect.Install));

        reconnect.Landed.ShouldBeTrue("fixture check: the reconnect landed after the removal had read the row and its dependents");
        refusal.Message.ShouldContain("reconnected");

        var row = await _kit.LoadCredentialAsync(dead);

        row.DeletedDate.ShouldBeNull("the person had just reconnected it; removing it under them would lose the authorization they were told succeeded");
        row.Status.ShouldBe(CredentialStatus.Active);
        row.EncryptedPayload.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task The_lists_dependent_count_is_what_the_dependents_read_finds()
    {
        var world = await _kit.SeedWorldAsync();
        var busy = await DisconnectedTokenAsync(world, world.MemberId);
        var quiet = await DisconnectedTokenAsync(world, world.MemberId);
        var live = await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-live");

        await _kit.SeedRepositoryAsync(world, busy, RepositoryStatus.Error);
        await _kit.SeedRepositoryAsync(world, busy, RepositoryStatus.Error);
        await _kit.SeedConnectionWebhookAsync(world, busy, RepositoryWebhookRegistrationStatus.DeadLettered);
        await _kit.SeedConnectionWebhookAsync(world, busy, RepositoryWebhookRegistrationStatus.Cancelled);
        await SeedIdentityAsync(world, world.MemberId, busy);
        await SoftDeleteIdentityAsync(await SeedIdentityAsync(world, world.AdminId, busy));
        await SoftDeleteRepositoryAsync(await _kit.SeedRepositoryAsync(world, busy, RepositoryStatus.Error));
        await _kit.SeedRepositoryAsync(world, live);

        var listed = (await ListAsync(world, world.MemberId)).ToDictionary(c => c.Id);

        foreach (var id in new[] { busy, quiet, live })
        {
            var dependents = await DependentsAsync(world, world.MemberId, id);

            listed[id].DependentCount.ShouldBe(dependents.Repositories.Count + dependents.ConnectionWebhooks.Count + dependents.Identities.Count, $"{id}: one rule for what leans on a credential, whichever read asks");
            listed[id].BoundRepositoryCount.ShouldBe(dependents.Repositories.Count, $"{id}: the per-kind counts are the dependents read's own lists");
            listed[id].BoundConnectionWebhookCount.ShouldBe(dependents.ConnectionWebhooks.Count, $"{id}: a dead row held only by a group hook says so, so the dialog can name what keeps it listed and move it");
            listed[id].ActAsIdentityCount.ShouldBe(dependents.Identities.Count, $"{id}: an act-as identity is named too");
        }

        listed[busy].DependentCount.ShouldBe(4, "two live repositories, one in-service hook and one live identity — not the removed repository, the cancelled hook or the ended identity");
        (listed[busy].BoundRepositoryCount, listed[busy].BoundConnectionWebhookCount, listed[busy].ActAsIdentityCount).ShouldBe((2, 1, 1), "by kind: two repositories, the dead-lettered hook and the live identity");
        listed[quiet].DependentCount.ShouldBe(0);
        listed[live].DependentCount.ShouldBe(1, "a live credential's repository leans on it too: the count is not only for dead rows");
    }

    // ── Doors ─────────────────────────────────────────────────────────────────────

    private async Task<RemoveDisconnectedCredentialResult> RemoveAsync(BindingMoveKit.World world, Guid userId, Guid credentialId, Action<ContainerBuilder>? configure = null)
    {
        using var scope = _kit.Scope(world, userId, configure);

        return await scope.Resolve<IMediator>().Send(new RemoveDisconnectedCredentialCommand { CredentialId = credentialId });
    }

    private async Task<IReadOnlyList<CredentialSummary>> ListAsync(BindingMoveKit.World world, Guid userId)
    {
        using var scope = _kit.Scope(world, userId);

        return await scope.Resolve<IMediator>().Send(new ListCredentialsQuery());
    }

    private async Task<CredentialDependents> DependentsAsync(BindingMoveKit.World world, Guid userId, Guid credentialId)
    {
        using var scope = _kit.Scope(world, userId);

        return await scope.Resolve<IMediator>().Send(new GetCredentialDependentsQuery { CredentialId = credentialId });
    }

    /// <summary>"Use a token", then "Disconnect" — the person's own credential, dead the way the Disconnect leaves it.</summary>
    private async Task<Guid> DisconnectedTokenAsync(BindingMoveKit.World world, Guid userId)
    {
        var token = await _kit.LinkTokenAsync(world, userId, $"glpat-{Guid.NewGuid():N}");

        await _kit.DisconnectAsync(world, userId, token);

        return token;
    }

    /// <summary>"Connect with OAuth", then "Disconnect" — through the provider's callback, as a person connects.</summary>
    private async Task<Guid> DisconnectedOAuthAsync(BindingMoveKit.World world, Guid userId)
    {
        var oauth = await _kit.ConnectOAuthAsync(world, userId, $"at-{Guid.NewGuid():N}");

        await _kit.DisconnectAsync(world, userId, oauth);

        return oauth;
    }

    /// <summary>"Add team token" (owner-less, team-owned), disconnected by an Admin.</summary>
    private async Task<Guid> DisconnectedTeamTokenAsync(BindingMoveKit.World world)
    {
        var token = await _kit.AddTokenAsync(world, world.AdminId, $"glpat-{Guid.NewGuid():N}", ownership: CredentialOwnership.TeamService);

        await _kit.DisconnectAsync(world, world.AdminId, token);

        return token;
    }

    private static Guid IdOf(BindingMoveKit.World world, Who who) => who switch
    {
        Who.Member => world.MemberId,
        Who.Viewer => world.ViewerId,
        Who.Admin => world.AdminId,
        _ => world.OwnerId
    };

    // ── Seeds ─────────────────────────────────────────────────────────────────────

    private async Task LeanOnAsync(BindingMoveKit.World world, Guid credentialId, Lean lean)
    {
        switch (lean)
        {
            case Lean.Repository: await _kit.SeedRepositoryAsync(world, credentialId, RepositoryStatus.Error); break;
            case Lean.ConnectionWebhook: await _kit.SeedConnectionWebhookAsync(world, credentialId, RepositoryWebhookRegistrationStatus.Registered); break;
            default: await SeedIdentityAsync(world, world.MemberId, credentialId); break;
        }
    }

    private async Task<Guid> SeedIdentityAsync(BindingMoveKit.World world, Guid userId, Guid credentialId)
    {
        var identity = new UserProviderIdentity { Id = Guid.NewGuid(), UserId = userId, ProviderInstanceId = world.InstanceId, CredentialId = credentialId, ProviderUserId = $"gl-{Guid.NewGuid():N}"[..10], ProviderUsername = $"gl-user-{Guid.NewGuid():N}"[..14] };

        using var scope = _kit.Fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.UserProviderIdentity.Add(identity);
        await db.SaveChangesAsync();

        return identity.Id;
    }

    private async Task SoftDeleteRepositoryAsync(Guid id)
    {
        using var scope = _kit.Fixture.BeginScope();

        await scope.Resolve<CodeSpaceDbContext>().Repository.Where(r => r.Id == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.DeletedDate, DateTimeOffset.UtcNow));
    }

    private async Task SoftDeleteIdentityAsync(Guid id)
    {
        using var scope = _kit.Fixture.BeginScope();

        await scope.Resolve<CodeSpaceDbContext>().UserProviderIdentity.Where(i => i.Id == id).ExecuteUpdateAsync(s => s.SetProperty(i => i.DeletedDate, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// A person reconnecting the credential in place — the production re-authorization write, on its own connection — at the
    /// moment the removal has finished reading the credential and what leans on it and has not yet written: the window a check-then-write
    /// would lose. Installed as a decorator of the dependents read, which is the last thing the removal asks before it writes.
    /// </summary>
    private sealed class ReconnectOnce
    {
        private readonly BindingMoveKit _kit;
        private readonly BindingMoveKit.World _world;
        private readonly Guid _credentialId;

        public ReconnectOnce(BindingMoveKit kit, BindingMoveKit.World world, Guid credentialId)
        {
            _kit = kit;
            _world = world;
            _credentialId = credentialId;
        }

        public bool Landed { get; private set; }

        public void Install(ContainerBuilder builder)
        {
            builder.RegisterInstance(this).AsSelf().SingleInstance();
            builder.RegisterDecorator<ReconnectingDependentsRead, ICredentialRelinkService>();
        }

        public async Task LandAsync()
        {
            using var scope = _kit.Fixture.BeginScope();
            var sealedToken = scope.Resolve<IPayloadEncryptor>().Encrypt(scope.Resolve<ICredentialPayloadSerializer>().Serialize(new PatPayload { Token = "glpat-reconnected" }));

            Landed = await scope.Resolve<ICredentialReauthorizationWriter>().TryReplaceAsync(new CredentialReplacement { CredentialId = _credentialId, ExpectedEncryptedPayload = string.Empty, EncryptedPayload = sealedToken, AuthType = AuthType.Pat, ModifiedBy = _world.MemberId }, CancellationToken.None);
        }
    }

    private sealed class ReconnectingDependentsRead : ICredentialRelinkService
    {
        private readonly ICredentialRelinkService _inner;
        private readonly ReconnectOnce _reconnect;

        public ReconnectingDependentsRead(ICredentialRelinkService inner, ReconnectOnce reconnect)
        {
            _inner = inner;
            _reconnect = reconnect;
        }

        public async Task<CredentialDependents> GetDependentsAsync(Guid credentialId, CancellationToken cancellationToken)
        {
            var dependents = await _inner.GetDependentsAsync(credentialId, cancellationToken);

            await _reconnect.LandAsync();

            return dependents;
        }

        public Task<IReadOnlyList<RelinkSuggestion>> GetSuggestionsAsync(Guid credentialId, CancellationToken cancellationToken) => _inner.GetSuggestionsAsync(credentialId, cancellationToken);

        public Task<IReadOnlyList<RelinkSuggestion>> GetCandidatesAsync(Guid providerInstanceId, Guid? leavingCredentialId, CancellationToken cancellationToken) => _inner.GetCandidatesAsync(providerInstanceId, leavingCredentialId, cancellationToken);

        public Task<RelinkCredentialBindingsResult> RelinkAllAsync(Guid fromCredentialId, Guid toCredentialId, CancellationToken cancellationToken) => _inner.RelinkAllAsync(fromCredentialId, toCredentialId, cancellationToken);
    }
}
