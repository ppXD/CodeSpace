using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Outbox.Payloads;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Messages.Dtos.Providers;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Outbox.Handlers;

public sealed class RegisterWebhookOutboxHandler : IOutboxMessageHandler, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IProviderRegistry _registry;
    private readonly IPayloadEncryptor _encryptor;

    public RegisterWebhookOutboxHandler(CodeSpaceDbContext db, IProviderRegistry registry, IPayloadEncryptor encryptor)
    {
        _db = db;
        _registry = registry;
        _encryptor = encryptor;
    }

    public string MessageType => OutboxMessageTypes.RegisterWebhook;

    public async Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var payload = DeserializePayload(message);
        var repository = await LoadRepositoryAsync(payload.RepositoryId, cancellationToken).ConfigureAwait(false);
        var providerContext = new ProviderContext(repository.ProviderInstance, repository.Credential!);

        var remote = await ResolveRemoteRepositoryAsync(repository, providerContext, cancellationToken).ConfigureAwait(false);
        var registered = await CallRemoteRegisterAsync(repository, providerContext, remote, payload, cancellationToken).ConfigureAwait(false);

        _db.RepositoryWebhook.Add(BuildWebhookEntity(payload, registered));
    }

    private static RegisterWebhookOutboxPayload DeserializePayload(OutboxMessage message)
    {
        return JsonSerializer.Deserialize<RegisterWebhookOutboxPayload>(message.Payload)
            ?? throw new InvalidOperationException($"OutboxMessage {message.Id} payload is not a valid {nameof(RegisterWebhookOutboxPayload)}");
    }

    private async Task<Repository> LoadRepositoryAsync(Guid repositoryId, CancellationToken cancellationToken)
    {
        var repo = await _db.Repository
            .Include(r => r.ProviderInstance)
            .Include(r => r.Credential)
            .SingleOrDefaultAsync(r => r.Id == repositoryId && r.DeletedDate == null, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Repository {repositoryId} not found or deleted — cannot register webhook");

        if (repo.Credential == null) throw new InvalidOperationException($"Repository {repositoryId} has no credential bound — cannot register webhook");

        return repo;
    }

    private async Task<RemoteRepository> ResolveRemoteRepositoryAsync(Repository repository, ProviderContext providerContext, CancellationToken cancellationToken)
    {
        var catalog = _registry.Require<IRepositoryCatalogCapability>(repository.ProviderInstance.Provider);
        return await catalog.GetByExternalIdAsync(providerContext, repository.ExternalId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RemoteWebhook> CallRemoteRegisterAsync(Repository repository, ProviderContext providerContext, RemoteRepository remote, RegisterWebhookOutboxPayload payload, CancellationToken cancellationToken)
    {
        var webhookCap = _registry.Require<IWebhookRegistrationCapability>(repository.ProviderInstance.Provider);
        var registration = new WebhookRegistration
        {
            CallbackUrl = payload.CallbackUrl,
            Secret = payload.Secret,
            SubscribedEvents = payload.SubscribedEvents
        };

        return await webhookCap.RegisterWebhookAsync(providerContext, remote, registration, cancellationToken).ConfigureAwait(false);
    }

    private RepositoryWebhook BuildWebhookEntity(RegisterWebhookOutboxPayload payload, RemoteWebhook registered) => new()
    {
        Id = payload.WebhookId,
        RepositoryId = payload.RepositoryId,
        ExternalId = registered.ExternalId,
        CallbackUrl = registered.CallbackUrl,
        SecretEnc = _encryptor.Encrypt(payload.Secret),
        SubscribedEvents = registered.SubscribedEvents.ToList(),
        Active = true
    };
}
