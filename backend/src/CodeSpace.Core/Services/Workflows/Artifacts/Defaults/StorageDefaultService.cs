using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Workflows.Artifacts.Defaults.Exceptions;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts.Routing;
using CodeSpace.Messages.Commands.Storage;
using CodeSpace.Messages.Dtos.Storage;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Defaults;

/// <summary>
/// Deployment-admin control plane for the storage template. It has no artifact store, driver, workflow, agent,
/// harness or completion dependency, never decrypts a secret, and — because a template belongs to no team — never
/// takes or reads a team id.
///
/// <para><b>Nothing consumes what it writes.</b> The materializer lane is the intended reader.</para>
/// </summary>
public sealed class StorageDefaultService : IStorageDefaultService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IStorageProviderModuleCatalog _providers;
    private readonly IRoutedDataClassCatalog _dataClasses;
    private readonly IPayloadEncryptor _encryptor;
    private readonly TimeProvider _clock;

    public StorageDefaultService(CodeSpaceDbContext db, IStorageProviderModuleCatalog providers, IRoutedDataClassCatalog dataClasses, IPayloadEncryptor encryptor, TimeProvider clock)
    {
        _db = db;
        _providers = providers;
        _dataClasses = dataClasses;
        _encryptor = encryptor;
        _clock = clock;
    }

    public async Task<IReadOnlyList<StorageDefaultSummary>> ListAsync(CancellationToken cancellationToken)
    {
        var rows = await Rows().OrderBy(row => row.Template.DataClassTypeKey).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(row => Summary(row.Template, row.SafeHint)).ToList();
    }

    public async Task<StorageDefaultDetail?> GetAsync(Guid defaultId, CancellationToken cancellationToken)
    {
        var row = await Rows().SingleOrDefaultAsync(value => value.Template.Id == defaultId, cancellationToken).ConfigureAwait(false);
        return row == null ? null : Detail(row.Template, row.SafeHint);
    }

    public async Task<StorageDefaultDetail> CreateAsync(Guid actorId, CreateStorageDefaultCommand command, CancellationToken cancellationToken)
    {
        var prepared = PrepareTemplate(Input(command));
        if (await _db.StorageDefault.AsNoTracking().AnyAsync(value => value.DataClassTypeKey == prepared.DataClassTypeKey, cancellationToken).ConfigureAwait(false))
            throw new StorageDefaultConflictException($"A deployment storage default for data class '{prepared.DataClassTypeKey}' already exists.");

        var now = _clock.GetUtcNow();
        var template = NewTemplate(prepared, actorId, now);
        template.IsEnabled = command.IsEnabled;
        template.CredentialId = AttachCredential(command.Secret, prepared.ProviderTypeKey, command.SafeHint, actorId, now);
        _db.StorageDefault.Add(template);

        await SaveAsync($"A deployment storage default for data class '{prepared.DataClassTypeKey}' already exists.", cancellationToken).ConfigureAwait(false);
        return await RequireAsync(template.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<StorageDefaultDetail?> UpdateAsync(Guid actorId, UpdateStorageDefaultCommand command, CancellationToken cancellationToken)
    {
        var template = await _db.StorageDefault.SingleOrDefaultAsync(value => value.Id == command.DefaultId, cancellationToken).ConfigureAwait(false);
        if (template == null) return null;
        EnsureExpected(template, command.ExpectedXmin, command.ExpectedRevision);

        var prepared = PrepareTemplate(Input(command, template.DataClassTypeKey));
        var now = _clock.GetUtcNow();
        var credentialId = await ResolveCredentialAsync(template, command, actorId, now).ConfigureAwait(false);
        Apply(template, prepared, actorId, now);
        template.Revision = checked(template.Revision + 1);
        template.CredentialId = credentialId;

        await SaveAsync("The deployment storage default changed before this edit could be applied.", cancellationToken).ConfigureAwait(false);
        return await RequireAsync(template.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<StorageDefaultDetail?> SetEnabledAsync(Guid actorId, SetStorageDefaultEnabledCommand command, CancellationToken cancellationToken)
    {
        var template = await _db.StorageDefault.SingleOrDefaultAsync(value => value.Id == command.DefaultId, cancellationToken).ConfigureAwait(false);
        if (template == null) return null;
        EnsureExpected(template, command.ExpectedXmin, command.ExpectedRevision);
        if (template.IsEnabled == command.IsEnabled) return await RequireAsync(template.Id, cancellationToken).ConfigureAwait(false);

        // Deliberately does NOT advance the revision. That number is stamped into a materialized team's provenance to
        // answer "which template EDIT produced this team's profile", and a disable/enable cycle produces no different
        // profile — bumping it would report every already-current team as stale. The xmin token still moves, so
        // concurrent edits are caught exactly as they are elsewhere.
        template.IsEnabled = command.IsEnabled;
        template.LastModifiedDate = _clock.GetUtcNow();
        template.LastModifiedBy = actorId;

        await SaveAsync("The deployment storage default changed before it could be enabled or disabled.", cancellationToken).ConfigureAwait(false);
        return await RequireAsync(template.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Admission for one authored template. The data class must be one this build actually routes — a key nothing
    /// asks for would be configured storage no consumer ever reads — and its adoption policy must be one that class
    /// is allowed to declare.
    /// </summary>
    private PreparedTemplate PrepareTemplate(TemplateInput input)
    {
        var dataClassTypeKey = ExecuteRule(() => StorageDefaultRules.NormalizeDataClassTypeKey(input.DataClassTypeKey));
        var dataClass = _dataClasses.Get(dataClassTypeKey)
            ?? throw new StorageDefaultInvalidException($"No consumer in this build reads data class '{dataClassTypeKey}', so a deployment default for it would never be used.");

        var module = RequireModule(input.ProviderTypeKey);
        var policy = (StorageDefaultAdoptionPolicy)(int)input.AdoptionPolicy;
        ExecuteRule(() => StorageDefaultRules.EnsureAdoptionPolicyAllowed(dataClass, policy));
        ExecuteRule(() => StorageDefaultRules.ValidatePartialConfig(input.NonSecretConfig, module));

        var canonicalConfig = ExecuteRule(() => StorageDefaultRules.CanonicalJson(input.NonSecretConfig));
        var namespaceRoot = ExecuteRule(() => StorageDefaultRules.NormalizeNamespaceRoot(input.NamespaceRoot));
        return new PreparedTemplate(dataClassTypeKey, module.TypeKey, canonicalConfig, namespaceRoot, policy);
    }

    /// <summary>Appends one instance-scope envelope and returns its id, or null when the command carries no secret.</summary>
    private Guid? AttachCredential(JsonElement? secret, string providerTypeKey, string? safeHint, Guid actorId, DateTimeOffset now)
    {
        if (secret is not { } value) return null;

        var module = RequireModule(providerTypeKey);
        var prepared = PrepareCredential(module, value, safeHint);
        var credential = new StorageDefaultCredential
        {
            Id = Guid.NewGuid(), ProviderTypeKey = module.TypeKey, EncryptedPayload = prepared.EncryptedPayload,
            SafeHint = prepared.SafeHint, EnvelopeFingerprint = prepared.EnvelopeFingerprint, CreatedDate = now, CreatedBy = actorId,
        };
        _db.StorageDefaultCredential.Add(credential);
        return credential.Id;
    }

    /// <summary>
    /// Which envelope the edited template points at: a newly appended one when a secret is supplied, none when the
    /// operator clears it, otherwise the one it already had. Superseded envelopes are never overwritten in place.
    ///
    /// <para>A RETAINED envelope must still belong to the template's provider. Repointing a template at a different
    /// provider while silently keeping the old provider's secret would leave a template that reads as complete and
    /// cannot possibly work, and the failure would only surface for whichever team the materializer reached first.</para>
    /// </summary>
    private async Task<Guid?> ResolveCredentialAsync(StorageDefault template, UpdateStorageDefaultCommand command, Guid actorId, DateTimeOffset now)
    {
        if (command.Secret is { } && command.ClearCredential)
            throw new StorageDefaultInvalidException("Supply a Secret or set ClearCredential, never both.");
        if (command.ClearCredential) return null;
        if (AttachCredential(command.Secret, command.ProviderTypeKey, command.SafeHint, actorId, now) is { } appended) return appended;
        if (template.CredentialId is not { } retained) return null;

        await EnsureRetainedCredentialMatchesAsync(retained, command.ProviderTypeKey).ConfigureAwait(false);
        return retained;
    }

    private async Task EnsureRetainedCredentialMatchesAsync(Guid credentialId, string providerTypeKey)
    {
        var module = RequireModule(providerTypeKey);
        var envelopeProvider = await _db.StorageDefaultCredential.AsNoTracking()
            .Where(value => value.Id == credentialId).Select(value => value.ProviderTypeKey).SingleAsync().ConfigureAwait(false);

        if (!string.Equals(envelopeProvider, module.TypeKey, StringComparison.Ordinal))
            throw new StorageDefaultInvalidException($"The attached credential belongs to provider '{envelopeProvider}' but this edit points the template at '{module.TypeKey}'. Supply a Secret for the new provider, or set ClearCredential.");
    }

    private PreparedCredential PrepareCredential(IStorageProviderModule module, JsonElement secret, string? safeHint)
    {
        if (secret.ValueKind != JsonValueKind.Object) throw new StorageDefaultInvalidException("Secret must be a JSON object.");

        try
        {
            StorageProviderJson.Validate(secret, module.SecretSchema, "Secret");
            var encrypted = _encryptor.Encrypt(StorageProviderJson.Canonicalize(secret, "Secret"));
            return new PreparedCredential(encrypted, Credentials.StorageCredentialRules.NormalizeSafeHint(safeHint), Credentials.StorageCredentialRules.EnvelopeFingerprint(encrypted));
        }
        catch (ArgumentException exception)
        {
            throw new StorageDefaultInvalidException(exception.Message, exception);
        }
    }

    private IStorageProviderModule RequireModule(string providerTypeKey)
    {
        try { return _providers.Require(providerTypeKey); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException) { throw new StorageDefaultInvalidException(exception.Message, exception); }
    }

    private IQueryable<TemplateRow> Rows() =>
        from template in _db.StorageDefault.AsNoTracking()
        join credential in _db.StorageDefaultCredential.AsNoTracking() on template.CredentialId equals credential.Id into attached
        from credential in attached.DefaultIfEmpty()
        select new TemplateRow { Template = template, SafeHint = credential == null ? null : credential.SafeHint };

    private async Task<StorageDefaultDetail> RequireAsync(Guid defaultId, CancellationToken cancellationToken) =>
        await GetAsync(defaultId, cancellationToken).ConfigureAwait(false)
        ?? throw new StorageDefaultConflictException("The deployment storage default disappeared while it was being written.");

    private async Task SaveAsync(string conflictMessage, CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new StorageDefaultConflictException(conflictMessage, exception);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            throw new StorageDefaultConflictException(conflictMessage, exception);
        }
    }

    private static void Apply(StorageDefault template, PreparedTemplate prepared, Guid actorId, DateTimeOffset now)
    {
        template.ProviderTypeKey = prepared.ProviderTypeKey;
        template.NonSecretConfigJson = prepared.NonSecretConfigJson;
        template.NamespaceRoot = prepared.NamespaceRoot;
        template.AdoptionPolicy = prepared.AdoptionPolicy;
        template.LastModifiedDate = now;
        template.LastModifiedBy = actorId;
    }

    private static StorageDefault NewTemplate(PreparedTemplate prepared, Guid actorId, DateTimeOffset now)
    {
        var template = new StorageDefault { Id = Guid.NewGuid(), DataClassTypeKey = prepared.DataClassTypeKey, Revision = 1, CreatedDate = now, CreatedBy = actorId };
        Apply(template, prepared, actorId, now);
        return template;
    }

    private static void EnsureExpected(StorageDefault template, uint expectedXmin, int expectedRevision)
    {
        if (template.Xmin != expectedXmin || template.Revision != expectedRevision)
            throw new StorageDefaultConflictException($"Deployment storage default version mismatch: expected xmin {expectedXmin} at revision {expectedRevision}, current xmin is {template.Xmin} at revision {template.Revision}.");
    }

    private static TemplateInput Input(CreateStorageDefaultCommand command) =>
        new(command.DataClassTypeKey, command.ProviderTypeKey, command.NonSecretConfig, command.NamespaceRoot, command.AdoptionPolicy);

    private static TemplateInput Input(UpdateStorageDefaultCommand command, string dataClassTypeKey) =>
        new(dataClassTypeKey, command.ProviderTypeKey, command.NonSecretConfig, command.NamespaceRoot, command.AdoptionPolicy);

    private static StorageDefaultSummary Summary(StorageDefault template, string? safeHint) => new()
    {
        Id = template.Id, DataClassTypeKey = template.DataClassTypeKey, Revision = template.Revision,
        ProviderTypeKey = template.ProviderTypeKey, AdoptionPolicy = Policy(template.AdoptionPolicy), IsEnabled = template.IsEnabled,
        HasCredential = template.CredentialId != null, CredentialSafeHint = safeHint, Xmin = template.Xmin,
        CreatedDate = template.CreatedDate, LastModifiedDate = template.LastModifiedDate,
    };

    private static StorageDefaultDetail Detail(StorageDefault template, string? safeHint) => new()
    {
        Id = template.Id, DataClassTypeKey = template.DataClassTypeKey, Revision = template.Revision,
        ProviderTypeKey = template.ProviderTypeKey, NonSecretConfig = Parse(template.NonSecretConfigJson), NamespaceRoot = template.NamespaceRoot,
        AdoptionPolicy = Policy(template.AdoptionPolicy), IsEnabled = template.IsEnabled, HasCredential = template.CredentialId != null,
        CredentialSafeHint = safeHint, Xmin = template.Xmin, CreatedDate = template.CreatedDate, CreatedBy = template.CreatedBy,
        LastModifiedDate = template.LastModifiedDate, LastModifiedBy = template.LastModifiedBy,
    };

    private static StorageDefaultAdoptionPolicyValue Policy(StorageDefaultAdoptionPolicy policy) => (StorageDefaultAdoptionPolicyValue)(int)policy;

    private static T ExecuteRule<T>(Func<T> action)
    {
        try { return action(); }
        catch (ArgumentException exception) { throw new StorageDefaultInvalidException(exception.Message, exception); }
    }

    private static void ExecuteRule(Action action)
    {
        try { action(); }
        catch (ArgumentException exception) { throw new StorageDefaultInvalidException(exception.Message, exception); }
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static bool IsUniqueViolation(DbUpdateException exception) => exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private sealed record TemplateInput(string DataClassTypeKey, string ProviderTypeKey, JsonElement NonSecretConfig, string NamespaceRoot, StorageDefaultAdoptionPolicyValue AdoptionPolicy);

    private sealed record PreparedTemplate(string DataClassTypeKey, string ProviderTypeKey, string NonSecretConfigJson, string NamespaceRoot, StorageDefaultAdoptionPolicy AdoptionPolicy);

    private sealed record PreparedCredential(string EncryptedPayload, string? SafeHint, string EnvelopeFingerprint);

    private sealed class TemplateRow
    {
        public required StorageDefault Template { get; init; }
        public string? SafeHint { get; init; }
    }
}
