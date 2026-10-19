using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.IntegrationTests.OAuth;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Credentials;

/// <summary>What a repository's move to another connection touches and what it keeps, whichever door asked for it, and what it is refused over.</summary>
public sealed partial class SameHostConnectionsFlowTests
{
    /// <summary>The two doors a person relinks through: one repository, or everything a dead credential held.</summary>
    public enum Door { RelinkOne, RelinkAll }

    private async Task MoveAsync(Door door, BindingMoveKit.World world, Guid userId, Guid dead, Guid repoId, Guid target, CredentialRelinkFlowTests.ProbeRecorder? provider = null)
    {
        if (door == Door.RelinkOne) await RelinkOneAsync(world, userId, repoId, target, provider);
        else await RelinkAllAsync(world, userId, dead, target, provider);
    }

    [Theory]
    [InlineData(Door.RelinkOne)]
    [InlineData(Door.RelinkAll)]
    public async Task A_move_keeps_the_repositorys_identity_its_project_links_and_its_triggers_and_leaves_the_credential_owners_act_as_row_as_it_was(Door door)
    {
        var world = await _kit.SeedWorldAsync();
        var sibling = await _kit.SeedSiblingInstanceAsync(world);
        var dead = await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-dead", CredentialStatus.Revoked);
        var repoId = await _kit.SeedRepositoryAsync(world, dead, RepositoryStatus.Error);
        var target = await _kit.SeedCredentialAsync(world, world.AdminId, "glpat-target", instanceId: sibling);
        var projectId = await SeedProjectLinkAsync(world, repoId);
        var activationId = await SeedActivationAsync(world, repoId);
        var identityId = await SeedIdentityAsync(world.AdminId, sibling, target);
        var before = await _kit.LoadRepositoryAsync(repoId);
        var activationBefore = await LoadActivationConfigAsync(activationId);

        await MoveAsync(door, world, world.AdminId, dead, repoId, target);

        var after = await _kit.LoadRepositoryAsync(repoId);

        after.Id.ShouldBe(before.Id, "the same row: its id is what every project link, trigger and history row points at");
        (after.ExternalId, after.FullPath, after.WebUrl, after.CreatedDate).ShouldBe((before.ExternalId, before.FullPath, before.WebUrl, before.CreatedDate));
        after.ProviderInstanceId.ShouldBe(sibling);
        (await LoadLinkAsync(projectId, repoId)).DeletedDate.ShouldBeNull("the project still has its repository");
        (await LoadActivationConfigAsync(activationId)).ShouldBe(activationBefore, "a trigger that names the repository keeps naming it, by the id that did not change");

        var identity = await LoadIdentityAsync(identityId);

        identity.DeletedDate.ShouldBeNull();
        (identity.CredentialId, identity.ProviderInstanceId).ShouldBe((target, sibling), "the act-as row of the person whose credential took the repository over is untouched by what was bound through it — what a move does to the OTHER people's act-as is the candidate-list test's");
    }

    [Theory]
    [InlineData(Door.RelinkOne)]
    [InlineData(Door.RelinkAll)]
    public async Task A_repositorys_hooks_are_registered_again_through_the_new_connection_and_the_old_ones_retired(Door door)
    {
        var world = await _kit.SeedWorldAsync();
        var sibling = await _kit.SeedSiblingInstanceAsync(world);
        var dead = await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-dead", CredentialStatus.Revoked);
        var repoId = await _kit.SeedRepositoryAsync(world, dead, RepositoryStatus.Error);
        var target = await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-target", instanceId: sibling);
        var registered = await _kit.SeedRepositoryWebhookAsync(repoId, RepositoryWebhookRegistrationStatus.Registered);
        var failed = await _kit.SeedRepositoryWebhookAsync(repoId, RepositoryWebhookRegistrationStatus.Failed);
        var registeredAtProvider = (await _kit.LoadRepositoryWebhooksAsync(repoId)).Single(w => w.Id == registered).ExternalId!;

        using var dispatches = _kit.Jobs().ManualExecution();
        var provider = new CredentialRelinkFlowTests.ProbeRecorder();

        await MoveAsync(door, world, world.OwnerId, dead, repoId, target, provider);

        var hooks = await _kit.LoadRepositoryWebhooksAsync(repoId);
        var staged = hooks.Single(w => w.Id != registered && w.Id != failed);

        hooks.ShouldNotContain(w => w.Id == registered, "a hook that was registered described the old connection's registration, and is gone");
        hooks.Single(w => w.Id == failed).RegistrationStatus.ShouldBe(RepositoryWebhookRegistrationStatus.Cancelled, "one that never registered is taken out of service, so no registrar completes it behind the move");
        staged.RegistrationStatus.ShouldBe(RepositoryWebhookRegistrationStatus.Enqueued, "a fresh hook, handed to the registrar once the move committed");
        staged.CallbackUrl.ShouldEndWith(staged.Id.ToString(), Case.Insensitive, "the callback carries the hook's own id, as a bind's does");
        staged.ExternalId.ShouldBeNull();
        _kit.WasRegistrationDispatched(staged.Id).ShouldBeTrue();
        provider.Deletions.Select(d => $"{d.Credential}|{d.HookExternalId}").ShouldBe(new[] { $"{target}|{registeredAtProvider}" }, "the provider is asked to delete the old hook, as the credential that took the repository over");
    }

    [Fact]
    public async Task A_connection_wide_target_gets_one_owner_hook_for_every_repository_under_the_owner_and_the_repositorys_own_hooks_are_retired()
    {
        var world = await _kit.SeedWorldAsync();
        var sibling = await _kit.SeedSiblingInstanceAsync(world, webhookScope: ProviderWebhookScope.Connection);
        var dead = await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-dead", CredentialStatus.Revoked);
        var first = await _kit.SeedRepositoryAsync(world, dead, RepositoryStatus.Error);
        var second = await _kit.SeedRepositoryAsync(world, dead, RepositoryStatus.Error);
        var target = await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-target", instanceId: sibling);
        var perRepository = await _kit.SeedRepositoryWebhookAsync(first, RepositoryWebhookRegistrationStatus.Registered);

        using var dispatches = _kit.Jobs().ManualExecution();
        var provider = new CredentialRelinkFlowTests.ProbeRecorder();

        await RelinkAllAsync(world, world.OwnerId, dead, target, provider);

        var owners = await LoadConnectionWebhooksAsync(sibling);

        owners.Count.ShouldBe(1, "two repositories under one owner are covered by one hook: a second would deliver every push twice");
        (owners[0].OwnerPath, owners[0].CredentialId).ShouldBe(("acme", target));
        _kit.WasDispatched(owners[0].Id).ShouldBeTrue("handed to the connection hook's registrar once the move committed");
        (await _kit.LoadRepositoryWebhooksAsync(first)).ShouldBeEmpty("the connection-wide hook covers it; its own hook is retired, or both would deliver");
        (await _kit.LoadRepositoryWebhooksAsync(second)).ShouldBeEmpty("and none is staged for the repository that had none");
        provider.Deletions.Count.ShouldBe(1);
        (await _kit.LoadRepositoryWebhooksAsync(first)).ShouldNotContain(w => w.Id == perRepository);
    }

    [Fact]
    public async Task A_repository_leaving_a_connection_wide_connection_for_a_per_repository_one_gets_a_hook_of_its_own_and_the_old_groups_hook_stays()
    {
        var world = await _kit.SeedWorldAsync();

        await _kit.SetWebhookScopeAsync(world.InstanceId, ProviderWebhookScope.Connection);

        var sibling = await _kit.SeedSiblingInstanceAsync(world);
        var dead = await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-dead", CredentialStatus.Revoked);
        var repoId = await _kit.SeedRepositoryAsync(world, dead, RepositoryStatus.Error);
        var other = await _kit.SeedRepositoryAsync(world, dead, RepositoryStatus.Error);
        var target = await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-target", instanceId: sibling);
        var groupHook = await _kit.SeedConnectionWebhookAsync(world, dead, RepositoryWebhookRegistrationStatus.Registered);

        using var dispatches = _kit.Jobs().ManualExecution();

        await RelinkOneAsync(world, world.OwnerId, repoId, target);

        var staged = (await _kit.LoadRepositoryWebhooksAsync(repoId)).ShouldHaveSingleItem("the connection it arrives at registers per repository, so the repository gets its own hook");

        staged.RegistrationStatus.ShouldBe(RepositoryWebhookRegistrationStatus.Enqueued);
        _kit.WasRegistrationDispatched(staged.Id).ShouldBeTrue();
        (await _kit.LoadConnectionWebhookAsync(groupHook)).RegistrationStatus.ShouldBe(RepositoryWebhookRegistrationStatus.Registered, "the group hook on the connection it left still covers what stays there: one repository leaving does not retire it");
        (await _kit.LoadRepositoryAsync(other)).CredentialId.ShouldBe(dead);
    }

    [Fact]
    public async Task A_connection_wide_target_that_already_covers_the_owner_stages_no_second_hook()
    {
        var world = await _kit.SeedWorldAsync();
        var sibling = await _kit.SeedSiblingInstanceAsync(world, webhookScope: ProviderWebhookScope.Connection);
        var dead = await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-dead", CredentialStatus.Revoked);
        var repoId = await _kit.SeedRepositoryAsync(world, dead, RepositoryStatus.Error);
        var target = await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-target", instanceId: sibling);
        var covering = await _kit.SeedConnectionWebhookAsync(world, target, RepositoryWebhookRegistrationStatus.Registered, instanceId: sibling);

        using var dispatches = _kit.Jobs().ManualExecution();

        await RelinkAllAsync(world, world.OwnerId, dead, target);

        (await LoadConnectionWebhooksAsync(sibling)).Select(h => h.Id).ShouldBe(new[] { covering });
        _kit.WasDispatched(covering).ShouldBeFalse("a hook that already delivers is left alone");
    }

    [Theory]
    [InlineData(Door.RelinkOne)]
    [InlineData(Door.RelinkAll)]
    public async Task A_move_onto_a_connection_that_already_holds_the_project_is_refused_and_moves_nothing(Door door)
    {
        var world = await _kit.SeedWorldAsync();
        var sibling = await _kit.SeedSiblingInstanceAsync(world);
        var dead = await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-dead", CredentialStatus.Revoked);
        var target = await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-target", instanceId: sibling);
        var repoId = await _kit.SeedRepositoryAsync(world, dead, RepositoryStatus.Error, name: "api");
        await _kit.SeedRepositoryAsync(world, target, instanceId: sibling, name: "api");
        var hook = await _kit.SeedRepositoryWebhookAsync(repoId, RepositoryWebhookRegistrationStatus.Registered);

        var refusal = await Record.ExceptionAsync(() => MoveAsync(door, world, world.OwnerId, dead, repoId, target));

        refusal.ShouldBeOfType<InvalidOperationException>(refusal?.Message);
        refusal.Message.ShouldContain("already bound under", Case.Sensitive, "the refusal says what is in the way, in place of a unique-index violation");
        var repo = await _kit.LoadRepositoryAsync(repoId);
        (repo.CredentialId, repo.ProviderInstanceId, repo.Status).ShouldBe((dead, world.InstanceId, RepositoryStatus.Error));
        (await _kit.LoadRepositoryWebhooksAsync(repoId)).Select(w => w.Id).ShouldBe(new[] { hook }, "nor were its hooks touched");
    }

    [Theory]
    [InlineData(Door.RelinkOne)]
    [InlineData(Door.RelinkAll)]
    public async Task A_credential_whose_scopes_cannot_register_the_connections_hook_is_refused_before_anything_moves(Door door)
    {
        var world = await _kit.SeedWorldAsync();
        var sibling = await _kit.SeedSiblingInstanceAsync(world);
        var dead = await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-dead", CredentialStatus.Revoked);
        var repoId = await _kit.SeedRepositoryAsync(world, dead, RepositoryStatus.Error);
        var readOnly = await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-read-only", instanceId: sibling, scopes: new[] { "read_user" });
        var provider = new CredentialRelinkFlowTests.ProbeRecorder();

        var refusal = await Record.ExceptionAsync(() => MoveAsync(door, world, world.OwnerId, dead, repoId, readOnly, provider));

        refusal.ShouldBeOfType<InvalidOperationException>(refusal?.Message);
        refusal.Message.ShouldContain("cannot take", Case.Sensitive);
        (await _kit.LoadRepositoryAsync(repoId)).CredentialId.ShouldBe(dead);
        provider.Reads.ShouldBeEmpty("refused on what the credential was granted, before the provider is asked anything");

        var sufficient = await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-api", instanceId: sibling, scopes: new[] { "api" });

        await MoveAsync(door, world, world.OwnerId, dead, repoId, sufficient);

        (await _kit.LoadRepositoryAsync(repoId)).CredentialId.ShouldBe(sufficient, "the same move with the scopes the hook needs goes through");
    }

    [Fact]
    public async Task The_target_is_asked_to_read_every_repository_it_takes_to_another_connection_as_itself()
    {
        var world = await _kit.SeedWorldAsync();
        var sibling = await _kit.SeedSiblingInstanceAsync(world);
        var dead = await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-dead", CredentialStatus.Revoked);
        var first = await _kit.SeedRepositoryAsync(world, dead, RepositoryStatus.Error, name: "first");
        var second = await _kit.SeedRepositoryAsync(world, dead, RepositoryStatus.Error, name: "second");
        var target = await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-target", instanceId: sibling);
        var provider = new CredentialRelinkFlowTests.ProbeRecorder();

        await RelinkAllAsync(world, world.OwnerId, dead, target, provider);

        provider.Reads.OrderBy(r => r.ExternalId).Select(r => $"{r.Credential}|{r.ExternalId}").ShouldBe(new[] { $"{target}|acme/first", $"{target}|acme/second" }, "each repository, once, as the credential that takes it");
        (await _kit.LoadRepositoryAsync(first)).CredentialId.ShouldBe(target);
        (await _kit.LoadRepositoryAsync(second)).CredentialId.ShouldBe(target);
    }

    [Theory]
    [InlineData(Door.RelinkOne)]
    [InlineData(Door.RelinkAll)]
    public async Task A_target_that_cannot_read_the_repository_refuses_a_move_to_another_connection_and_names_it(Door door)
    {
        var world = await _kit.SeedWorldAsync();
        var sibling = await _kit.SeedSiblingInstanceAsync(world);
        var dead = await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-dead", CredentialStatus.Revoked);
        var repoId = await _kit.SeedRepositoryAsync(world, dead, RepositoryStatus.Error, name: "hidden");
        var target = await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-target", instanceId: sibling);
        var hook = await _kit.SeedRepositoryWebhookAsync(repoId, RepositoryWebhookRegistrationStatus.Registered);
        var provider = new CredentialRelinkFlowTests.ProbeRecorder(refuseFor: "hidden");

        var refusal = await Record.ExceptionAsync(() => MoveAsync(door, world, world.OwnerId, dead, repoId, target, provider));

        refusal.ShouldBeOfType<InvalidOperationException>(refusal?.Message);
        refusal.Message.ShouldContain("acme/hidden", Case.Sensitive, "the refusal names what the target cannot read");
        var repo = await _kit.LoadRepositoryAsync(repoId);
        (repo.CredentialId, repo.ProviderInstanceId).ShouldBe((dead, world.InstanceId));
        (await _kit.LoadRepositoryWebhooksAsync(repoId)).Select(w => w.Id).ShouldBe(new[] { hook }, "and nothing about its hooks changed");
    }

    [Theory]
    [InlineData(Door.RelinkOne)]
    [InlineData(Door.RelinkAll)]
    public async Task A_repository_the_target_finds_at_another_path_is_not_moved_to_another_connection(Door door)
    {
        var world = await _kit.SeedWorldAsync();
        var sibling = await _kit.SeedSiblingInstanceAsync(world);
        var dead = await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-dead", CredentialStatus.Revoked);
        var repoId = await _kit.SeedRepositoryAsync(world, dead, RepositoryStatus.Error, name: "renamed", storedPath: "acme/old-name");
        var target = await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-target", instanceId: sibling);

        var refusal = await Record.ExceptionAsync(() => MoveAsync(door, world, world.OwnerId, dead, repoId, target, new CredentialRelinkFlowTests.ProbeRecorder()));

        refusal.ShouldBeOfType<InvalidOperationException>(refusal?.Message);
        refusal.Message.ShouldContain("acme/old-name", Case.Sensitive, "it names the path CodeSpace has");
        refusal.Message.ShouldContain("acme/renamed", Case.Sensitive, "and the one the provider gave");
        (await _kit.LoadRepositoryAsync(repoId)).CredentialId.ShouldBe(dead);
    }

    [Theory]
    [InlineData(Door.RelinkOne)]
    [InlineData(Door.RelinkAll)]
    public async Task A_move_within_one_connection_never_checks_the_path_and_touches_no_hook(Door door)
    {
        var world = await _kit.SeedWorldAsync();
        var dead = await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-dead", CredentialStatus.Revoked);
        var repoId = await _kit.SeedRepositoryAsync(world, dead, RepositoryStatus.Error, name: "renamed", storedPath: "acme/old-name");
        var target = await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-target");
        var hook = await _kit.SeedRepositoryWebhookAsync(repoId, RepositoryWebhookRegistrationStatus.Registered);
        var provider = new CredentialRelinkFlowTests.ProbeRecorder();

        await MoveAsync(door, world, world.OwnerId, dead, repoId, target, provider);

        var repo = await _kit.LoadRepositoryAsync(repoId);
        (repo.CredentialId, repo.ProviderInstanceId, repo.Status).ShouldBe((target, world.InstanceId, RepositoryStatus.Active));
        (await _kit.LoadRepositoryWebhooksAsync(repoId)).Select(w => (w.Id, w.RegistrationStatus)).ShouldBe(new[] { (hook, RepositoryWebhookRegistrationStatus.Registered) }, "the registered hook is exactly as it was: only another connection's move registers a hook again");
        provider.Deletions.ShouldBeEmpty();
        provider.Reads.Count.ShouldBe(door == Door.RelinkAll ? 1 : 0, "a single relink within one connection reads nothing, as it never did; relink-all keeps the lookup it always made");
    }

    [Fact]
    public async Task A_provider_that_does_not_answer_is_a_retryable_refusal_that_blames_nobody()
    {
        var world = await _kit.SeedWorldAsync();
        var sibling = await _kit.SeedSiblingInstanceAsync(world);
        var dead = await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-dead", CredentialStatus.Revoked);
        var repoId = await _kit.SeedRepositoryAsync(world, dead, RepositoryStatus.Error);
        var target = await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-target", instanceId: sibling);
        var provider = new CredentialRelinkFlowTests.ProbeRecorder(refuseFor: "", failure: () => new HttpRequestException("connection reset by peer"));

        var refusal = await Record.ExceptionAsync(() => RelinkOneAsync(world, world.OwnerId, repoId, target, provider));

        refusal.ShouldBeOfType<RelinkVerificationUnavailableException>(refusal?.Message);
        refusal.Message.ShouldContain("Try again", Case.Sensitive);
        refusal.Message.ShouldNotContain("cannot read", Case.Insensitive, "nothing was learned about the target");
        (await _kit.LoadRepositoryAsync(repoId)).CredentialId.ShouldBe(dead);
    }

    public enum Refused { AnotherApiPath, ADifferentApiRoot }

    [Theory]
    [InlineData(Door.RelinkOne, Refused.AnotherApiPath)]
    [InlineData(Door.RelinkAll, Refused.AnotherApiPath)]
    [InlineData(Door.RelinkOne, Refused.ADifferentApiRoot)]
    [InlineData(Door.RelinkAll, Refused.ADifferentApiRoot)]
    public async Task A_connection_to_another_host_is_still_refused_with_the_same_message(Door door, Refused shape)
    {
        var world = await _kit.SeedWorldAsync();
        var other = shape == Refused.AnotherApiPath ? await _kit.SeedSiblingInstanceAsync(world, apiUrl: $"https://gitlab-{Guid.NewGuid():N}.test/other") : await _kit.SeedOtherInstanceAsync(world);
        var dead = await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-dead", CredentialStatus.Revoked);
        var repoId = await _kit.SeedRepositoryAsync(world, dead, RepositoryStatus.Error);
        var target = await _kit.SeedCredentialAsync(world, world.OwnerId, "glpat-elsewhere", instanceId: other);

        var refusal = await Record.ExceptionAsync(() => MoveAsync(door, world, world.OwnerId, dead, repoId, target));

        refusal.ShouldBeOfType<InvalidOperationException>(refusal?.Message);
        refusal.Message.ShouldContain("is for a different provider instance", Case.Sensitive, shape.ToString());
        refusal.Message.ShouldContain("Pick a credential connected to the same provider", Case.Sensitive);
        (await _kit.LoadRepositoryAsync(repoId)).CredentialId.ShouldBe(dead);
    }

    [Fact]
    public async Task Another_persons_personal_credential_never_takes_a_repository_to_another_connection_but_a_team_token_does()
    {
        var world = await _kit.SeedWorldAsync();
        var sibling = await _kit.SeedSiblingInstanceAsync(world);
        var dead = await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-dead", CredentialStatus.Revoked);
        var repoId = await _kit.SeedRepositoryAsync(world, dead, RepositoryStatus.Error);
        var teammates = await _kit.SeedCredentialAsync(world, world.MemberId, "glpat-teammates", instanceId: sibling);
        var teams = await _kit.SeedCredentialAsync(world, ownerId: null, "glpat-team", ownership: CredentialOwnership.TeamService, instanceId: sibling);

        var refusal = await Record.ExceptionAsync(() => RelinkOneAsync(world, world.AdminId, repoId, teammates));

        refusal.ShouldBeOfType<InvalidOperationException>(refusal?.Message);
        refusal.Message.ShouldContain("another person's personal connection", Case.Sensitive);
        (await _kit.LoadRepositoryAsync(repoId)).CredentialId.ShouldBe(dead);

        await RelinkOneAsync(world, world.AdminId, repoId, teams);

        (await _kit.LoadRepositoryAsync(repoId)).CredentialId.ShouldBe(teams);
    }

    // ── Reads and seeds ───────────────────────────────────────────────────────────

    private async Task<List<ConnectionWebhook>> LoadConnectionWebhooksAsync(Guid instanceId)
    {
        using var scope = _kit.Fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().ConnectionWebhook.AsNoTracking().Where(h => h.ProviderInstanceId == instanceId && h.RegistrationStatus != RepositoryWebhookRegistrationStatus.Cancelled).ToListAsync();
    }

    private async Task<Guid> SeedProjectLinkAsync(BindingMoveKit.World world, Guid repositoryId)
    {
        var project = new Project { Id = Guid.NewGuid(), TeamId = world.TeamId, Name = "Kept", Slug = $"kept-{Guid.NewGuid():N}"[..14] };

        using var scope = _kit.Fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.Project.Add(project);
        db.ProjectRepository.Add(new ProjectRepository { ProjectId = project.Id, RepositoryId = repositoryId, TeamId = world.TeamId });
        await db.SaveChangesAsync();

        return project.Id;
    }

    private async Task<ProjectRepository> LoadLinkAsync(Guid projectId, Guid repositoryId)
    {
        using var scope = _kit.Fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().ProjectRepository.AsNoTracking().SingleAsync(l => l.ProjectId == projectId && l.RepositoryId == repositoryId);
    }

    private async Task<Guid> SeedActivationAsync(BindingMoveKit.World world, Guid repositoryId)
    {
        var workflow = new Workflow { Id = Guid.NewGuid(), TeamId = world.TeamId, Slug = $"wf-{Guid.NewGuid():N}"[..14], Name = "Reviews", DefinitionJson = "{}" };
        var activation = new WorkflowActivation { Id = Guid.NewGuid(), WorkflowId = workflow.Id, TypeKey = "trigger.pull_request", ConfigJson = JsonSerializer.Serialize(new { repositoryIds = new[] { repositoryId } }), Enabled = true };

        using var scope = _kit.Fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.Workflow.Add(workflow);
        db.WorkflowActivation.Add(activation);
        await db.SaveChangesAsync();

        return activation.Id;
    }

    private async Task<string> LoadActivationConfigAsync(Guid activationId)
    {
        using var scope = _kit.Fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().WorkflowActivation.AsNoTracking().Where(a => a.Id == activationId && a.DeletedDate == null).Select(a => a.ConfigJson).SingleAsync();
    }

    private async Task<Guid> SeedIdentityAsync(Guid userId, Guid instanceId, Guid credentialId)
    {
        var identity = new UserProviderIdentity { Id = Guid.NewGuid(), UserId = userId, ProviderInstanceId = instanceId, CredentialId = credentialId, ProviderUserId = $"gl-{Guid.NewGuid():N}"[..10], ProviderUsername = $"gl-user-{Guid.NewGuid():N}"[..14] };

        using var scope = _kit.Fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.UserProviderIdentity.Add(identity);
        await db.SaveChangesAsync();

        return identity.Id;
    }

    private async Task<UserProviderIdentity> LoadIdentityAsync(Guid id)
    {
        using var scope = _kit.Fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().UserProviderIdentity.AsNoTracking().SingleAsync(i => i.Id == id);
    }
}
