using System.Text.Json;
using Autofac;
using CodeSpace.Core.Authorization;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows.Artifacts.Defaults.Exceptions;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Storage;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Storage;
using CodeSpace.Messages.Queries.Storage;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Storage;

/// <summary>
/// The deployment-template control plane driven through the real MediatR pipeline against real Postgres: the instance
/// capability gate, the revision/xmin rules, the encrypted envelope, and the owner's adoption-policy decision.
///
/// <para>Nothing consumes a template yet — the materializer lane is the intended reader — so nothing here asserts that
/// any team's storage changed, because none does.</para>
///
/// <para>A template is instance scoped, unique per data class, and its row cannot be deleted, so this suite may
/// successfully create AT MOST ONE template per real data class across the whole fixture. Every other case therefore
/// uses a path that creates no row (a refusal) or a synthetic key.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class StorageDefaultFlowTests
{
    private readonly PostgresFixture _fixture;

    public StorageDefaultFlowTests(PostgresFixture fixture) => _fixture = fixture;

    /// <summary>
    /// Agent Run logs have no local home, so their template may be adopted automatically. The whole authoring
    /// lifecycle in one flow, because the row it creates can never be removed for a second test to recreate.
    /// </summary>
    [Fact]
    public async Task Deployment_admin_authors_a_template_whose_secret_round_trips_as_ciphertext()
    {
        var actorId = await SeedActorAsync();
        const string secret = """{"accessKeyId":"AK","accessKeySecret":"shhh-never-in-a-column"}""";

        var created = await SendAsync(actorId, new CreateStorageDefaultCommand
        {
            DataClassTypeKey = "agent-run-log/v1", ProviderTypeKey = "aliyun-oss/v1",
            NonSecretConfig = Json("""{"endpoint":"oss-cn-hangzhou.aliyuncs.com","bucket":"codespace-artifacts"}"""),
            NamespaceRoot = "codespace-defaults/", AdoptionPolicy = StorageDefaultAdoptionPolicyValue.Automatic,
            IsEnabled = true, Secret = Json(secret), SafeHint = "AK…1234",
        });

        created.Revision.ShouldBe(1);
        created.AdoptionPolicy.ShouldBe(StorageDefaultAdoptionPolicyValue.Automatic);
        created.NamespaceRoot.ShouldBe("codespace-defaults/");
        created.HasCredential.ShouldBeTrue();
        created.CredentialSafeHint.ShouldBe("AK…1234");
        JsonSerializer.Serialize(created).ShouldNotContain("shhh-never-in-a-column");

        await AssertEnvelopeAsync(created.Id, secret);

        var duplicate = async () => await SendAsync(actorId, Create("agent-run-log/v1", StorageDefaultAdoptionPolicyValue.Automatic));
        await duplicate.ShouldThrowAsync<StorageDefaultConflictException>();

        var updated = await SendAsync(actorId, new UpdateStorageDefaultCommand
        {
            DefaultId = created.Id, ExpectedXmin = created.Xmin, ExpectedRevision = created.Revision,
            ProviderTypeKey = "aliyun-oss/v1",
            NonSecretConfig = Json("""{"endpoint":"oss-cn-hangzhou.aliyuncs.com","bucket":"codespace-artifacts-v2"}"""),
            NamespaceRoot = "codespace-defaults-v2/", AdoptionPolicy = StorageDefaultAdoptionPolicyValue.Automatic,
        });

        updated.ShouldNotBeNull();
        updated.Revision.ShouldBe(2);
        updated.NamespaceRoot.ShouldBe("codespace-defaults-v2/");
        updated.HasCredential.ShouldBeTrue("omitting Secret must keep the attached envelope, not silently drop it");

        var repointed = async () => await SendAsync(actorId, new UpdateStorageDefaultCommand
        {
            DefaultId = created.Id, ExpectedXmin = updated.Xmin, ExpectedRevision = updated.Revision,
            ProviderTypeKey = "local-rwx/v1", NonSecretConfig = Json("{}"),
            NamespaceRoot = "/srv/codespace/agent-run-logs", AdoptionPolicy = StorageDefaultAdoptionPolicyValue.Automatic,
        });
        var mismatch = await repointed.ShouldThrowAsync<StorageDefaultInvalidException>();
        mismatch.Message.ShouldContain("aliyun-oss/v1", customMessage: "keeping one provider's secret on a template repointed at another provider produces a template that reads complete and cannot work");

        var stale = async () => await SendAsync(actorId, new UpdateStorageDefaultCommand
        {
            DefaultId = created.Id, ExpectedXmin = created.Xmin, ExpectedRevision = created.Revision,
            ProviderTypeKey = "aliyun-oss/v1", NonSecretConfig = Json("""{"endpoint":"oss-cn-hangzhou.aliyuncs.com","bucket":"stale"}"""),
            NamespaceRoot = "stale/", AdoptionPolicy = StorageDefaultAdoptionPolicyValue.Automatic,
        });
        await stale.ShouldThrowAsync<StorageDefaultConflictException>();

        var disabled = await SendAsync(actorId, new SetStorageDefaultEnabledCommand
        {
            DefaultId = created.Id, ExpectedXmin = updated.Xmin, ExpectedRevision = updated.Revision, IsEnabled = false,
        });
        disabled.ShouldNotBeNull();
        disabled.IsEnabled.ShouldBeFalse();
        disabled.Revision.ShouldBe(2, "toggling a template off is not a content edit, so it must not report every already-materialized team as stale");
        disabled.Xmin.ShouldNotBe(updated.Xmin, "the concurrency token must still move, or a lost update goes unnoticed");

        (await SendAsync(actorId, new ListStorageDefaultsQuery())).ShouldContain(row => row.Id == created.Id);
    }

    /// <summary>
    /// The owner's decision, enforced rather than remembered. Workflow artifacts keep a local backend, and
    /// materializing the class takes that away for good — an Active route cannot return to Draft, Retired is terminal,
    /// and a route cannot be deleted — so no operator may configure it to happen on a team's first write.
    /// </summary>
    [Fact]
    public async Task Workflow_artifacts_cannot_be_adopted_automatically_but_may_be_adopted_explicitly()
    {
        var actorId = await SeedActorAsync();

        var automatic = async () => await SendAsync(actorId, Create("workflow-artifact/v1", StorageDefaultAdoptionPolicyValue.Automatic));

        var refusal = await automatic.ShouldThrowAsync<StorageDefaultInvalidException>();
        refusal.Message.ShouldContain("workflow-artifact/v1");
        await AssertNoTemplateAsync("workflow-artifact/v1");

        var explicitly = await SendAsync(actorId, Create("workflow-artifact/v1", StorageDefaultAdoptionPolicyValue.Explicit));

        explicitly.AdoptionPolicy.ShouldBe(StorageDefaultAdoptionPolicyValue.Explicit);
        explicitly.HasCredential.ShouldBeFalse();
    }

    /// <summary>
    /// The instance capability is the whole gate. A caller who does not hold it is refused before the handler runs, so
    /// no row is written — which is what a deployment-level write must do for every account that is not a deployment
    /// admin.
    /// </summary>
    [Fact]
    public async Task A_caller_without_the_capability_is_refused_and_writes_nothing()
    {
        var actorId = await SeedActorAsync();
        var dataClassTypeKey = "agent-run-log/v1";

        using var scope = _fixture.BeginScope(builder => builder
            .RegisterInstance(new TestCurrentUser(actorId, "no-capability"))
            .As<ICurrentUser>().SingleInstance());

        var act = async () => await scope.Resolve<IMediator>().Send(Create(dataClassTypeKey, StorageDefaultAdoptionPolicyValue.Automatic));

        var denied = await act.ShouldThrowAsync<TenantAccessDeniedException>();
        denied.Reason.ShouldContain(Permissions.StorageDefaultsManage);

        var read = async () => await scope.Resolve<IMediator>().Send(new ListStorageDefaultsQuery());
        await read.ShouldThrowAsync<TenantAccessDeniedException>();
    }

    /// <summary>A template for a key no consumer reads would be configured storage nothing ever asks for.</summary>
    [Fact]
    public async Task A_data_class_this_build_does_not_route_is_refused()
    {
        var actorId = await SeedActorAsync();
        var unknown = $"t{Guid.NewGuid():N}/v1";

        var act = async () => await SendAsync(actorId, Create(unknown, StorageDefaultAdoptionPolicyValue.Explicit));

        await act.ShouldThrowAsync<StorageDefaultInvalidException>();
        await AssertNoTemplateAsync(unknown);
    }

    /// <summary>A namespace ROOT is mandatory: without one the materializer has nothing to append a per-team segment to.</summary>
    [Fact]
    public async Task A_blank_namespace_root_is_refused()
    {
        var actorId = await SeedActorAsync();

        var act = async () => await SendAsync(actorId, Create("agent-run-log/v1", StorageDefaultAdoptionPolicyValue.Automatic) with { NamespaceRoot = "   " });

        await act.ShouldThrowAsync<StorageDefaultInvalidException>();
    }

    private async Task AssertEnvelopeAsync(Guid defaultId, string expectedPlaintext)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var template = await db.StorageDefault.AsNoTracking().SingleAsync(value => value.Id == defaultId);
        var envelope = await db.StorageDefaultCredential.AsNoTracking().SingleAsync(value => value.Id == template.CredentialId);

        envelope.EncryptedPayload.ShouldNotContain("shhh-never-in-a-column");
        envelope.EnvelopeFingerprint.ShouldStartWith("sha256:");
        JsonDocument.Parse(scope.Resolve<IPayloadEncryptor>().Decrypt(envelope.EncryptedPayload)).RootElement
            .GetProperty("accessKeySecret").GetString().ShouldBe(JsonDocument.Parse(expectedPlaintext).RootElement.GetProperty("accessKeySecret").GetString());
    }

    private async Task AssertNoTemplateAsync(string dataClassTypeKey)
    {
        using var scope = _fixture.BeginScope();
        (await scope.Resolve<CodeSpaceDbContext>().StorageDefault.AsNoTracking().AnyAsync(value => value.DataClassTypeKey == dataClassTypeKey))
            .ShouldBeFalse($"a refused command must write nothing, but a template for '{dataClassTypeKey}' exists");
    }

    private async Task<TResponse> SendAsync<TResponse>(Guid actorId, IRequest<TResponse> request)
    {
        using var scope = _fixture.BeginScope(builder => builder
            .RegisterInstance(new TestCurrentUser(actorId, "deployment-admin") { Permissions = [Permissions.StorageDefaultsManage] })
            .As<ICurrentUser>().SingleInstance());
        return await scope.Resolve<IMediator>().Send(request);
    }

    private async Task<Guid> SeedActorAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var user = new User { Id = Guid.NewGuid(), Email = $"deployment-{suffix}@test.local", Name = "Deployment Admin" };
        db.User.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private static CreateStorageDefaultCommand Create(string dataClassTypeKey, StorageDefaultAdoptionPolicyValue policy) => new()
    {
        DataClassTypeKey = dataClassTypeKey, ProviderTypeKey = "local-rwx/v1", NonSecretConfig = Json("{}"),
        NamespaceRoot = "/srv/codespace/artifacts", AdoptionPolicy = policy, IsEnabled = true,
    };

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
