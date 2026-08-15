using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Workflows.Artifacts.Credentials.Exceptions;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Messages.Commands.Storage;
using CodeSpace.Messages.Dtos.Storage;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Credentials;

/// <summary>
/// Team-admin control plane for stable storage credentials and immutable encrypted revisions. It has no driver,
/// ArtifactStore, workflow, harness, completion, or model dependency and never decrypts a secret.
/// </summary>
public sealed class StorageCredentialService : IStorageCredentialService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IStorageProviderModuleCatalog _catalog;
    private readonly IPayloadEncryptor _encryptor;
    private readonly TimeProvider _clock;

    public StorageCredentialService(CodeSpaceDbContext db, IStorageProviderModuleCatalog catalog, IPayloadEncryptor encryptor, TimeProvider clock)
    {
        _db = db;
        _catalog = catalog;
        _encryptor = encryptor;
        _clock = clock;
    }

    public async Task<IReadOnlyList<StorageCredentialMetadata>> ListAsync(Guid teamId, CancellationToken cancellationToken)
    {
        var rows = await CurrentRows().Where(value => value.TeamId == teamId).OrderBy(value => value.StableName).ThenBy(value => value.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(Metadata).ToList();
    }

    public async Task<StoragePage<StorageCredentialMetadata>> ListPageAsync(Guid teamId, string? cursor, int limit, CancellationToken cancellationToken)
    {
        var keyset = StorageSettingsCursor.Decode(cursor);
        var take = Math.Clamp(limit, 1, StoragePageLimits.MaxPageSize);
        var query = CurrentRows().Where(value => value.TeamId == teamId);
        if (keyset is { } after)
            query = query.Where(value => string.Compare(value.StableName, after.StableName) > 0
                || (value.StableName == after.StableName && value.Id.CompareTo(after.Id) > 0));

        var rows = await query.OrderBy(value => value.StableName).ThenBy(value => value.Id).Take(take + 1)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var hasMore = rows.Count > take;
        var page = hasMore ? rows.GetRange(0, take) : rows;
        return new StoragePage<StorageCredentialMetadata>
        {
            Items = page.Select(Metadata).ToList(),
            NextCursor = hasMore ? new StorageSettingsCursor(page[^1].StableName, page[^1].Id).Encode() : null,
        };
    }

    public async Task<StorageCredentialMetadata?> GetAsync(Guid teamId, Guid credentialId, CancellationToken cancellationToken)
    {
        var row = await CurrentRows().SingleOrDefaultAsync(value => value.TeamId == teamId && value.Id == credentialId, cancellationToken).ConfigureAwait(false);
        return row == null ? null : Metadata(row);
    }

    public async Task<StorageCredentialMetadata> CreateAsync(Guid teamId, Guid actorId, CreateStorageCredentialCommand command, CancellationToken cancellationToken)
    {
        var stableName = ExecuteRule(() => StorageCredentialRules.NormalizeStableName(command.StableName));
        if (await _db.StorageCredential.AsNoTracking().AnyAsync(value => value.TeamId == teamId && value.StableName == stableName, cancellationToken).ConfigureAwait(false))
            throw new StorageCredentialConflictException($"Storage credential '{stableName}' already exists in this team.");

        var prepared = PrepareRevision(command.ProviderTypeKey, command.Secret, command.SafeHint);
        var now = _clock.GetUtcNow();
        var credential = new StorageCredential
        {
            Id = Guid.NewGuid(), TeamId = teamId, StableName = stableName, CurrentRevision = 1,
            State = StorageCredentialState.Active, CreatedDate = now, CreatedBy = actorId,
        };
        var revision = Revision(credential, 1, actorId, now, prepared);
        credential.Revisions.Add(revision);
        _db.StorageCredential.Add(credential);

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            throw new StorageCredentialConflictException($"Storage credential '{stableName}' already exists in this team.", exception);
        }

        return Metadata(Projection(credential, revision));
    }

    public async Task<StorageCredentialMetadata?> AppendRevisionAsync(Guid teamId, Guid actorId, AppendStorageCredentialRevisionCommand command, CancellationToken cancellationToken)
    {
        var credential = await _db.StorageCredential.SingleOrDefaultAsync(value => value.TeamId == teamId && value.Id == command.CredentialId, cancellationToken).ConfigureAwait(false);
        if (credential == null) return null;
        EnsureExpected(credential, command.ExpectedXmin, command.ExpectedCurrentRevision);
        ExecuteRule(() => StorageCredentialRules.EnsureRotationAllowed(credential.State));

        var prepared = PrepareRevision(command.ProviderTypeKey, command.Secret, command.SafeHint);
        var now = _clock.GetUtcNow();
        var nextRevision = checked(credential.CurrentRevision + 1);
        var revision = Revision(credential, nextRevision, actorId, now, prepared);
        _db.StorageCredentialRevision.Add(revision);
        credential.CurrentRevision = nextRevision;

        await SaveConcurrentAsync("The storage credential changed before this revision could be appended.", cancellationToken).ConfigureAwait(false);
        return Metadata(Projection(credential, revision));
    }

    public async Task<StorageCredentialMetadata?> RevokeAsync(Guid teamId, Guid actorId, RevokeStorageCredentialCommand command, CancellationToken cancellationToken)
    {
        var credential = await _db.StorageCredential.SingleOrDefaultAsync(value => value.TeamId == teamId && value.Id == command.CredentialId, cancellationToken).ConfigureAwait(false);
        if (credential == null) return null;
        EnsureExpected(credential, command.ExpectedXmin, command.ExpectedCurrentRevision);
        ExecuteRule(() => StorageCredentialRules.EnsureRevocationAllowed(credential.State));

        var current = await _db.StorageCredentialRevision.AsNoTracking()
            .Where(value => value.TeamId == teamId && value.StorageCredentialId == credential.Id && value.Revision == credential.CurrentRevision)
            .Select(value => new CurrentRevisionMetadata { ProviderTypeKey = value.ProviderTypeKey, SafeHint = value.SafeHint, CreatedDate = value.CreatedDate })
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        var now = _clock.GetUtcNow();
        credential.State = StorageCredentialState.Revoked;
        credential.RevokedDate = now;
        credential.RevokedBy = actorId;

        await SaveConcurrentAsync("The storage credential changed before it could be revoked.", cancellationToken).ConfigureAwait(false);
        return Metadata(Projection(credential, current));
    }

    private IQueryable<StorageCredentialProjection> CurrentRows() =>
        from credential in _db.StorageCredential.AsNoTracking()
        join revision in _db.StorageCredentialRevision.AsNoTracking()
            on new { credential.TeamId, StorageCredentialId = credential.Id, Revision = credential.CurrentRevision }
            equals new { revision.TeamId, revision.StorageCredentialId, revision.Revision }
        select new StorageCredentialProjection
        {
            Id = credential.Id, TeamId = credential.TeamId, StableName = credential.StableName, State = credential.State,
            CurrentRevision = credential.CurrentRevision, ProviderTypeKey = revision.ProviderTypeKey, SafeHint = revision.SafeHint,
            CreatedDate = credential.CreatedDate, CurrentRevisionCreatedDate = revision.CreatedDate,
            RevokedDate = credential.RevokedDate, Xmin = credential.Xmin,
        };

    private PreparedRevision PrepareRevision(string providerTypeKey, JsonElement secret, string? safeHint)
    {
        IStorageProviderModule module;
        try
        {
            module = _catalog.Require(providerTypeKey);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            throw new StorageCredentialInvalidException(exception.Message, exception);
        }

        if (secret.ValueKind != JsonValueKind.Object) throw new StorageCredentialInvalidException("Secret must be a JSON object.");
        try
        {
            StorageProviderJson.Validate(secret, module.SecretSchema, "Secret");
            var canonical = StorageProviderJson.Canonicalize(secret, "Secret");
            var encrypted = _encryptor.Encrypt(canonical);
            return new PreparedRevision(module.TypeKey, encrypted, StorageCredentialRules.NormalizeSafeHint(safeHint), StorageCredentialRules.EnvelopeFingerprint(encrypted));
        }
        catch (ArgumentException exception)
        {
            throw new StorageCredentialInvalidException(exception.Message, exception);
        }
    }

    private async Task SaveConcurrentAsync(string message, CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new StorageCredentialConflictException(message, exception);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception) || IsRevisionContention(exception))
        {
            throw new StorageCredentialConflictException(message, exception);
        }
    }

    private static void EnsureExpected(StorageCredential credential, uint expectedXmin, int expectedCurrentRevision)
    {
        if (credential.Xmin != expectedXmin || credential.CurrentRevision != expectedCurrentRevision)
            throw new StorageCredentialConflictException($"Storage credential version mismatch: expected xmin {expectedXmin} at revision {expectedCurrentRevision}, current xmin is {credential.Xmin} at revision {credential.CurrentRevision}.");
    }

    private static T ExecuteRule<T>(Func<T> action)
    {
        try { return action(); }
        catch (ArgumentException exception) { throw new StorageCredentialInvalidException(exception.Message, exception); }
    }

    private static void ExecuteRule(Action action)
    {
        try { action(); }
        catch (ArgumentException exception) { throw new StorageCredentialInvalidException(exception.Message, exception); }
    }

    private static StorageCredentialRevision Revision(StorageCredential credential, int revision, Guid actorId, DateTimeOffset now, PreparedRevision prepared) => new()
    {
        Id = Guid.NewGuid(), TeamId = credential.TeamId, StorageCredentialId = credential.Id, Revision = revision,
        ProviderTypeKey = prepared.ProviderTypeKey, EncryptedPayload = prepared.EncryptedPayload, SafeHint = prepared.SafeHint,
        EnvelopeFingerprint = prepared.EnvelopeFingerprint, CreatedDate = now, CreatedBy = actorId,
    };

    private static StorageCredentialProjection Projection(StorageCredential credential, StorageCredentialRevision revision) => new()
    {
        Id = credential.Id, TeamId = credential.TeamId, StableName = credential.StableName, State = credential.State,
        CurrentRevision = credential.CurrentRevision, ProviderTypeKey = revision.ProviderTypeKey, SafeHint = revision.SafeHint,
        CreatedDate = credential.CreatedDate, CurrentRevisionCreatedDate = revision.CreatedDate,
        RevokedDate = credential.RevokedDate, Xmin = credential.Xmin,
    };

    private static StorageCredentialProjection Projection(StorageCredential credential, CurrentRevisionMetadata revision) => new()
    {
        Id = credential.Id, TeamId = credential.TeamId, StableName = credential.StableName, State = credential.State,
        CurrentRevision = credential.CurrentRevision, ProviderTypeKey = revision.ProviderTypeKey, SafeHint = revision.SafeHint,
        CreatedDate = credential.CreatedDate, CurrentRevisionCreatedDate = revision.CreatedDate,
        RevokedDate = credential.RevokedDate, Xmin = credential.Xmin,
    };

    private static StorageCredentialMetadata Metadata(StorageCredentialProjection value) => new()
    {
        Id = value.Id, StableName = value.StableName, State = (StorageCredentialStateValue)(int)value.State,
        CurrentRevision = value.CurrentRevision, ProviderTypeKey = value.ProviderTypeKey, SafeHint = value.SafeHint,
        CredentialRef = StorageCredentialRules.CredentialRef(value.Id, value.CurrentRevision), CreatedDate = value.CreatedDate,
        CurrentRevisionCreatedDate = value.CurrentRevisionCreatedDate, RevokedDate = value.RevokedDate, Xmin = value.Xmin,
    };

    private static bool IsUniqueViolation(DbUpdateException exception) => exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private static bool IsRevisionContention(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: "P0001" } postgres
        && (postgres.MessageText.Contains("contiguous append-only sequence", StringComparison.Ordinal)
            || postgres.MessageText.Contains("Revoked state is terminal", StringComparison.Ordinal));

    private sealed class PreparedRevision
    {
        public PreparedRevision(string providerTypeKey, string encryptedPayload, string? safeHint, string envelopeFingerprint)
        {
            ProviderTypeKey = providerTypeKey;
            EncryptedPayload = encryptedPayload;
            SafeHint = safeHint;
            EnvelopeFingerprint = envelopeFingerprint;
        }

        public string ProviderTypeKey { get; }
        public string EncryptedPayload { get; }
        public string? SafeHint { get; }
        public string EnvelopeFingerprint { get; }
    }

    private sealed class CurrentRevisionMetadata
    {
        public required string ProviderTypeKey { get; init; }
        public string? SafeHint { get; init; }
        public required DateTimeOffset CreatedDate { get; init; }
    }

    private sealed class StorageCredentialProjection
    {
        public Guid Id { get; init; }
        public Guid TeamId { get; init; }
        public required string StableName { get; init; }
        public StorageCredentialState State { get; init; }
        public int CurrentRevision { get; init; }
        public required string ProviderTypeKey { get; init; }
        public string? SafeHint { get; init; }
        public DateTimeOffset CreatedDate { get; init; }
        public DateTimeOffset CurrentRevisionCreatedDate { get; init; }
        public DateTimeOffset? RevokedDate { get; init; }
        public uint Xmin { get; init; }
    }
}
