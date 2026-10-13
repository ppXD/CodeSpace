using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.Messages.Constants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeSpace.E2ETests.Storage;

/// <summary>
/// Rotating a deployment storage default's secret, and clearing it, through the real admin HTTP surface. Each rotation
/// appends a new envelope; the one it replaced used to stay decryptable forever, so a cloud key an operator rotated out
/// because it leaked stayed recoverable from every database copy. Now the envelope a template stops naming is blanked
/// in the same transaction, and only the one it names still opens.
///
/// <para>Fidelity: HIGH. The real <c>ApiUser</c> capability resolution, MediatR pipeline and its transaction, the
/// production encryptor, and Postgres with 0249's guard. Its own database (one fixture per class), because a template is
/// one row per data class for the whole deployment and can never be deleted.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class StorageDefaultSecretShredE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private readonly TaskLaunchApiFactory _factory;

    public StorageDefaultSecretShredE2ETests(TaskLaunchApiFactory factory) { _factory = factory; }

    [Fact]
    public async Task Rotating_then_clearing_a_templates_secret_leaves_only_the_envelope_it_names_decryptable()
    {
        var operatorId = await SeedCapabilityHolderAsync();
        var canary = Guid.NewGuid().ToString("N");

        var created = await SendAsync(operatorId, HttpMethod.Post, "/api/admin/storage-defaults", Template(new { accessKeyId = "AK", accessKeySecret = $"first-{canary}" }));
        var defaultId = created.GetProperty("id").GetGuid();
        var first = await NamedEnvelopeAsync(defaultId);

        var rotated = await SendAsync(operatorId, HttpMethod.Put, $"/api/admin/storage-defaults/{defaultId}", Edit(created, secret: new { accessKeyId = "AK", accessKeySecret = $"second-{canary}" }));
        var second = await NamedEnvelopeAsync(defaultId);

        second.ShouldNotBe(first, "fixture check: a supplied secret appends a new envelope");
        (await EnvelopeAsync(first!.Value)).ShouldBe(string.Empty, "the rotated-out key has no reader left and must not stay decryptable — check StorageDefaultService.UpdateAsync and 0249's storage_default_credential_guard");
        (await DecryptAsync(second!.Value)).ShouldContain($"second-{canary}", Case.Sensitive, "the envelope the template names is the one the materializer decrypts");

        var cleared = await SendAsync(operatorId, HttpMethod.Put, $"/api/admin/storage-defaults/{defaultId}", Edit(rotated, clearCredential: true));

        cleared.GetProperty("hasCredential").GetBoolean().ShouldBeFalse();
        (await NamedEnvelopeAsync(defaultId)).ShouldBeNull();
        (await EnvelopeAsync(second.Value)).ShouldBe(string.Empty, "a cleared key has no reader left either");
    }

    private static object Template(object secret) => new
    {
        dataClassTypeKey = "agent-run-log/v1",
        providerTypeKey = "aliyun-oss/v1",
        nonSecretConfig = new { endpoint = "oss-cn-hangzhou.aliyuncs.com", bucket = "codespace-artifacts" },
        namespaceRoot = "codespace-defaults/",
        adoptionPolicy = "Automatic",
        isEnabled = true,
        secret,
    };

    private static object Edit(JsonElement current, object? secret = null, bool clearCredential = false) => new
    {
        expectedXmin = current.GetProperty("xmin").GetUInt32(),
        expectedRevision = current.GetProperty("revision").GetInt32(),
        providerTypeKey = "aliyun-oss/v1",
        nonSecretConfig = new { endpoint = "oss-cn-hangzhou.aliyuncs.com", bucket = "codespace-artifacts" },
        namespaceRoot = "codespace-defaults/",
        adoptionPolicy = "Automatic",
        secret,
        clearCredential,
    };

    private async Task<JsonElement> SendAsync(Guid userId, HttpMethod method, string path, object body)
    {
        var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestToken.Mint(userId, TestToken.SeedStamp));

        var response = await _factory.CreateClient().SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK, $"{method} {path} got {(int)response.StatusCode}: {text}");

        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<Guid?> NamedEnvelopeAsync(Guid defaultId) =>
        await DbAsync(db => db.StorageDefault.AsNoTracking().Where(row => row.Id == defaultId).Select(row => row.CredentialId).SingleAsync());

    private async Task<string> EnvelopeAsync(Guid envelopeId) =>
        await DbAsync(db => db.StorageDefaultCredential.AsNoTracking().Where(row => row.Id == envelopeId).Select(row => row.EncryptedPayload).SingleAsync());

    private async Task<string> DecryptAsync(Guid envelopeId)
    {
        var envelope = await EnvelopeAsync(envelopeId);

        using var scope = _factory.Services.CreateScope();

        return scope.ServiceProvider.GetRequiredService<IPayloadEncryptor>().Decrypt(envelope);
    }

    private async Task<T> DbAsync<T>(Func<CodeSpaceDbContext, Task<T>> read)
    {
        using var scope = _factory.Services.CreateScope();

        return await read(scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>());
    }

    /// <summary>A deployment admin: an account holding storage.defaults.manage through a real role row, as <c>ApiUser</c> resolves it.</summary>
    private async Task<Guid> SeedCapabilityHolderAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var permissionId = await db.Permission.AsNoTracking().Where(value => value.Name == Permissions.StorageDefaultsManage).Select(value => value.Id).SingleAsync();
        var user = new User { Id = Guid.NewGuid(), SecurityStamp = TestToken.SeedStamp, Email = $"shred-operator-{suffix}@test.local", Name = "Deployment Admin", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId };
        var role = new Role { Id = Guid.NewGuid(), Name = $"storage-defaults-shred-{suffix}", IsSystem = false, Status = true };

        db.User.Add(user);
        db.Role.Add(role);
        db.RolePermission.Add(new RolePermission { Id = Guid.NewGuid(), RoleId = role.Id, PermissionId = permissionId });
        db.RoleUser.Add(new RoleUser { Id = Guid.NewGuid(), RoleId = role.Id, UserId = user.Id });
        await db.SaveChangesAsync();

        return user.Id;
    }
}
