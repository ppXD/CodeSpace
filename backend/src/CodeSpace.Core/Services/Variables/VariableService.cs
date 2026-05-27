using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Dtos.Variables;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Variables;

/// <summary>
/// EF + AES-GCM-backed implementation of <see cref="IVariableService"/>. One service
/// handles both Team and Workflow scopes — discrimination is on the row's <c>Scope</c>
/// column, not in the service signature.
///
/// <para>Rule 4 pipeline shape: every public method is a short sequence of named helper
/// calls; helpers fail by throwing or by returning null/empty. No inline branching trees.</para>
///
/// <para>Tenant boundary: this service does NOT itself enforce that the caller has access
/// to <paramref name="scopeId"/>. That gate lives in the MediatR pipeline behaviour
/// (the command/query carries an <c>IRequireTeamMembership</c> marker; <c>TeamMembershipResolver</c>
/// rejects mismatches). The service trusts its inputs and just persists/reads.</para>
/// </summary>
public sealed class VariableService : IVariableService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IVariableValueEncryption _encryption;
    private readonly ILogger<VariableService> _logger;

    public VariableService(CodeSpaceDbContext db, IVariableValueEncryption encryption, ILogger<VariableService> logger)
    {
        _db = db;
        _encryption = encryption;
        _logger = logger;
    }

    public async Task<IReadOnlyList<VariableSummary>> ListAsync(VariableScope scope, Guid scopeId, Guid expectedTeamId, CancellationToken cancellationToken)
    {
        await EnsureScopeBelongsToTeamAsync(scope, scopeId, expectedTeamId, cancellationToken).ConfigureAwait(false);
        var rows = await ActiveRowsFor(scope, scopeId).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(MapToSummary).ToList();
    }

    public async Task<VariableSummary?> GetAsync(VariableScope scope, Guid scopeId, Guid expectedTeamId, string name, CancellationToken cancellationToken)
    {
        await EnsureScopeBelongsToTeamAsync(scope, scopeId, expectedTeamId, cancellationToken).ConfigureAwait(false);
        var row = await LoadActiveRowAsync(scope, scopeId, name, cancellationToken).ConfigureAwait(false);
        return row == null ? null : MapToSummary(row);
    }

    public async Task<IReadOnlyList<ResolvedVariable>> GetAllForEngineAsync(VariableScope scope, Guid scopeId, CancellationToken cancellationToken)
    {
        // Engine path — caller (WorkflowEngine) has already loaded the run + workflow + team
        // by the time this is called. No additional tenant guard needed; the engine itself
        // is the tenancy boundary.
        var rows = await ActiveRowsFor(scope, scopeId).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(MapToResolved).ToList();
    }

    public async Task SetAsync(VariableScope scope, Guid scopeId, Guid expectedTeamId, string name, VariableValueType valueType, JsonElement value, string? description, Guid actorUserId, CancellationToken cancellationToken)
    {
        var teamId = await EnsureScopeBelongsToTeamAsync(scope, scopeId, expectedTeamId, cancellationToken).ConfigureAwait(false);
        var existing = await LoadActiveRowAsync(scope, scopeId, name, cancellationToken).ConfigureAwait(false);

        if (existing != null)
            RotateInPlace(existing, valueType, value, description, actorUserId);
        else
            CreateNew(scope, scopeId, teamId, name, valueType, value, description, actorUserId);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Variable upsert: scope={Scope} scopeId={ScopeId} name={Name} type={Type} (rotation={Rotation})",
            scope, scopeId, name, valueType, existing != null);
    }

    public async Task DeleteAsync(VariableScope scope, Guid scopeId, Guid expectedTeamId, string name, Guid actorUserId, CancellationToken cancellationToken)
    {
        await EnsureScopeBelongsToTeamAsync(scope, scopeId, expectedTeamId, cancellationToken).ConfigureAwait(false);
        var row = await LoadActiveRowAsync(scope, scopeId, name, cancellationToken).ConfigureAwait(false);
        if (row == null) return;   // idempotent — no-op on missing-name

        var now = DateTimeOffset.UtcNow;
        row.DeletedDate = now;
        row.LastModifiedDate = now;
        row.LastModifiedBy = actorUserId;

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Variable soft-deleted: scope={Scope} scopeId={ScopeId} name={Name}",
            scope, scopeId, name);
    }

    private IQueryable<Variable> ActiveRowsFor(VariableScope scope, Guid scopeId) =>
        _db.Variable.AsNoTracking().Where(v => v.Scope == scope && v.ScopeId == scopeId && v.DeletedDate == null);

    private async Task<Variable?> LoadActiveRowAsync(VariableScope scope, Guid scopeId, string name, CancellationToken cancellationToken) =>
        await _db.Variable
            .SingleOrDefaultAsync(v => v.Scope == scope && v.ScopeId == scopeId && v.Name == name && v.DeletedDate == null, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Tenant guard + team-id resolution in one pass. For Team scope, scopeId IS the team
    /// — assert it matches the caller's expected team. For Workflow scope, look up the
    /// workflow row with BOTH id AND team filters in one query — a workflow that doesn't
    /// exist OR belongs to a different team returns null and we throw
    /// <see cref="KeyNotFoundException"/>. Conflating not-found with not-authorized prevents
    /// cross-team enumeration (same pattern as <c>RepositoryAccessAuthorizationBehavior</c>).
    /// Returns the team id that the variable row should denormalise into the team_id
    /// column.
    /// </summary>
    private async Task<Guid> EnsureScopeBelongsToTeamAsync(VariableScope scope, Guid scopeId, Guid expectedTeamId, CancellationToken cancellationToken)
    {
        switch (scope)
        {
            case VariableScope.Team:
                if (scopeId != expectedTeamId)
                    throw new KeyNotFoundException($"Team {scopeId} not found or not accessible.");
                return expectedTeamId;

            case VariableScope.Workflow:
            {
                // Single query proves existence + ownership in one shot.
                var exists = await _db.Workflow.AsNoTracking()
                    .AnyAsync(w => w.Id == scopeId && w.TeamId == expectedTeamId && w.DeletedDate == null, cancellationToken)
                    .ConfigureAwait(false);
                if (!exists)
                    throw new KeyNotFoundException($"Workflow {scopeId} not found or not accessible.");
                return expectedTeamId;
            }

            case VariableScope.Project:
            {
                // Phase 3.0 — Project scope. Same proof-of-existence + ownership pattern.
                // A soft-deleted project's variables become unreferenceable (the engine's
                // slug→id resolver skips deleted rows), so we filter on deleted_date IS NULL.
                var exists = await _db.Project.AsNoTracking()
                    .AnyAsync(p => p.Id == scopeId && p.TeamId == expectedTeamId && p.DeletedDate == null, cancellationToken)
                    .ConfigureAwait(false);
                if (!exists)
                    throw new KeyNotFoundException($"Project {scopeId} not found or not accessible.");
                return expectedTeamId;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unknown variable scope");
        }
    }

    private void CreateNew(VariableScope scope, Guid scopeId, Guid teamId, string name, VariableValueType valueType, JsonElement value, string? description, Guid actorUserId)
    {
        var now = DateTimeOffset.UtcNow;
        var (plain, encrypted) = EncodeValue(valueType, value);

        _db.Variable.Add(new Variable
        {
            Id = Guid.NewGuid(),
            Scope = scope,
            ScopeId = scopeId,
            TeamId = teamId,
            Name = name,
            ValueType = valueType,
            ValuePlain = plain,
            ValueEncrypted = encrypted,
            Description = description,
            CreatedDate = now,
            CreatedBy = actorUserId,
            LastModifiedDate = now,
            LastModifiedBy = actorUserId,
        });
    }

    private void RotateInPlace(Variable existing, VariableValueType valueType, JsonElement value, string? description, Guid actorUserId)
    {
        var (plain, encrypted) = EncodeValue(valueType, value);
        existing.ValueType = valueType;
        existing.ValuePlain = plain;
        existing.ValueEncrypted = encrypted;
        existing.Description = description;
        existing.LastModifiedDate = DateTimeOffset.UtcNow;
        existing.LastModifiedBy = actorUserId;
    }

    /// <summary>
    /// Encodes the supplied <paramref name="value"/> into the (plain, encrypted) column
    /// pair appropriate for <paramref name="valueType"/>. Secret → encrypted only; everything
    /// else → JSON-stringified plain only. Exactly one of the two is non-null on return;
    /// matches the DB CHECK constraint.
    /// </summary>
    private (string? Plain, byte[]? Encrypted) EncodeValue(VariableValueType valueType, JsonElement value)
    {
        if (valueType != VariableValueType.Secret) return (value.GetRawText(), null);

        // Secret values must be a JSON string at the API boundary (operator types text into
        // a password field). We unwrap to the raw string before encrypting so the envelope
        // doesn't carry the JSON quotes — decrypt + JSON-wrap at read time keeps the engine
        // contract symmetric.
        var plaintext = value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : value.GetRawText();
        return (null, _encryption.Encrypt(plaintext));
    }

    private static VariableSummary MapToSummary(Variable row) => new()
    {
        Id = row.Id,
        Scope = row.Scope,
        ScopeId = row.ScopeId,
        TeamId = row.TeamId,
        Name = row.Name,
        ValueType = row.ValueType,
        ValuePlain = row.ValuePlain,   // Secret rows have null here by storage rule + DB CHECK
        Description = row.Description,
        CreatedDate = row.CreatedDate,
        LastModifiedDate = row.LastModifiedDate,
    };

    private ResolvedVariable MapToResolved(Variable row)
    {
        var value = row.ValueType == VariableValueType.Secret
            ? JsonSerializer.SerializeToElement(_encryption.Decrypt(row.ValueEncrypted!))
            : JsonDocument.Parse(row.ValuePlain!).RootElement.Clone();

        return new ResolvedVariable
        {
            Name = row.Name,
            ValueType = row.ValueType,
            Value = value,
        };
    }
}
