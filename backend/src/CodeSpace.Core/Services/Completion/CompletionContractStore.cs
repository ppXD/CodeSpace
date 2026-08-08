using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Contracts;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace CodeSpace.Core.Services.Completion;

public sealed class CompletionContractStore : ICompletionContractStore, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;

    public CompletionContractStore(CodeSpaceDbContext db) => _db = db;

    public async Task<IReadOnlyDictionary<(string RequirementRef, string Kind), long>> UpsertRequirementsAsync(Guid workflowRunId, Guid teamId, IReadOnlyList<RequirementEnvelope> requirements, CancellationToken cancellationToken)
    {
        if (requirements.Count == 0) return new Dictionary<(string, string), long>();

        var refs = requirements.Select(r => r.RequirementRef).ToList();
        var existing = await _db.CompletionRequirement
            .Where(r => r.WorkflowRunId == workflowRunId && r.TeamId == teamId && refs.Contains(r.RequirementRef))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var wroteAnything = false;

        foreach (var envelope in requirements)
        {
            var json = JsonSerializer.Serialize(envelope, AgentJson.Options);
            var row = existing.FirstOrDefault(r => r.RequirementRef == envelope.RequirementRef && r.Kind == envelope.Kind);

            if (row is null)
                _db.CompletionRequirement.Add(new CompletionRequirement
                {
                    Id = Guid.NewGuid(),
                    TeamId = teamId,
                    WorkflowRunId = workflowRunId,
                    RequirementRef = envelope.RequirementRef,
                    Kind = envelope.Kind,
                    EnvelopeJson = json,
                });
            else if (row.EnvelopeJson == json)
                continue;   // unchanged — no amendment, no revision
            else
                row.EnvelopeJson = json;   // an amended obligation overwrites its CURRENT envelope — the ref is the identity

            wroteAnything = true;

            // P1 (v4.3): the append-only history the in-place upsert used to destroy — one revision per first
            // stake and per amendment. Since #1321 the staked SpecHash is admission's comparand; without this,
            // the shape an earlier attempt was staked under vanished the moment a retry re-staked.
            _db.CompletionRequirementRevision.Add(new CompletionRequirementRevision
            {
                Id = Guid.NewGuid(),
                TeamId = teamId,
                WorkflowRunId = workflowRunId,
                RequirementRef = envelope.RequirementRef,
                Kind = envelope.Kind,
                EnvelopeJson = json,
            });
        }

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            // P2: a requirement AMENDMENT overwrites its row — the one contract-ledger write a watermark COUNT
            // cannot see — so a successful write advances the run's monotonic ledger version. The 23505 loser
            // below persisted nothing from this call and bumps nothing.
            if (wroteAnything) await CompletionLedgerVersionBump.BumpAsync(_db, workflowRunId, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            // A concurrent producer won the unique index — the obligation exists; upsert semantics hold. Detach
            // the losers (the revision rows they carried included) so this context's change tracker stays clean
            // for later saves.
            foreach (var entry in _db.ChangeTracker.Entries<CompletionRequirement>().Where(e => e.State == Microsoft.EntityFrameworkCore.EntityState.Added).ToList())
                entry.State = Microsoft.EntityFrameworkCore.EntityState.Detached;
            foreach (var entry in _db.ChangeTracker.Entries<CompletionRequirementRevision>().Where(e => e.State == Microsoft.EntityFrameworkCore.EntityState.Added).ToList())
                entry.State = Microsoft.EntityFrameworkCore.EntityState.Detached;
        }

        // The CURRENT revision per upserted key, read from the DATABASE rather than the change tracker on purpose:
        // one query answers identically for a fresh append (identity read-back), an idempotent replay (nothing
        // appended — the standing row wins), and the unique-index race path above (the winner's rows are the truth).
        var keys = requirements.Select(r => (r.RequirementRef, r.Kind)).ToHashSet();

        return (await CurrentRevisionsAsync(workflowRunId, teamId, refs, cancellationToken).ConfigureAwait(false))
            .Where(kvp => keys.Contains(kvp.Key))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    public Task<IReadOnlyDictionary<(string RequirementRef, string Kind), long>> GetCurrentRequirementRevisionsAsync(Guid workflowRunId, Guid teamId, CancellationToken cancellationToken) =>
        CurrentRevisionsAsync(workflowRunId, teamId, refs: null, cancellationToken);

    /// <summary>Max revision per (ref, kind) for one run — optionally narrowed to a ref list (the upsert's own keys).</summary>
    private async Task<IReadOnlyDictionary<(string RequirementRef, string Kind), long>> CurrentRevisionsAsync(Guid workflowRunId, Guid teamId, IReadOnlyList<string>? refs, CancellationToken cancellationToken)
    {
        var query = _db.CompletionRequirementRevision.AsNoTracking().Where(r => r.WorkflowRunId == workflowRunId && r.TeamId == teamId);

        if (refs is not null) query = query.Where(r => refs.Contains(r.RequirementRef));

        var rows = await query
            .GroupBy(r => new { r.RequirementRef, r.Kind })
            .Select(g => new { g.Key.RequirementRef, g.Key.Kind, Revision = g.Max(x => x.Revision) })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows.ToDictionary(r => (r.RequirementRef, r.Kind), r => r.Revision);
    }

    public async Task<IReadOnlyList<RequirementEnvelope>> ListRequirementRevisionsAsync(Guid workflowRunId, Guid teamId, string requirementRef, string kind, CancellationToken cancellationToken)
    {
        var rows = await _db.CompletionRequirementRevision.AsNoTracking()
            .Where(r => r.WorkflowRunId == workflowRunId && r.TeamId == teamId && r.RequirementRef == requirementRef && r.Kind == kind)
            .OrderBy(r => r.Revision)
            .Select(r => r.EnvelopeJson)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows.Select(json => JsonSerializer.Deserialize<RequirementEnvelope>(json, AgentJson.Options)!).ToList();
    }

    public async Task AppendReceiptAsync(Guid workflowRunId, Guid teamId, ReceiptEnvelope receipt, CancellationToken cancellationToken)
    {
        var row = new CompletionReceipt
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            WorkflowRunId = workflowRunId,
            RequirementRef = receipt.RequirementRef,
            Kind = receipt.Kind,
            AttemptId = receipt.AttemptId,
            TargetKey = receipt.TargetRef ?? $"attempt:{receipt.AttemptId}",
            EnvelopeJson = JsonSerializer.Serialize(receipt, AgentJson.Options),
            ObservedAt = receipt.ObservedAt,
        };

        _db.CompletionReceipt.Add(row);

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await CompletionLedgerVersionBump.BumpAsync(_db, workflowRunId, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            // Exactly-once: a crash-replayed producer re-appended the same logical receipt — the first row
            // stands. DETACH the loser or it poisons this context's change tracker for every later save.
            _db.Entry(row).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
        }
    }

    public async Task<IReadOnlyList<RequirementEnvelope>> ListRequirementsAsync(Guid workflowRunId, Guid teamId, CancellationToken cancellationToken) =>
        (await _db.CompletionRequirement.AsNoTracking()
            .Where(r => r.WorkflowRunId == workflowRunId && r.TeamId == teamId)
            .OrderBy(r => r.CreatedDate)
            .Select(r => r.EnvelopeJson)
            .ToListAsync(cancellationToken).ConfigureAwait(false))
        .Select(json => JsonSerializer.Deserialize<RequirementEnvelope>(json, AgentJson.Options)!)
        .ToList();

    public async Task<IReadOnlyList<ReceiptEnvelope>> ListReceiptsAsync(Guid workflowRunId, Guid teamId, CancellationToken cancellationToken) =>
        (await _db.CompletionReceipt.AsNoTracking()
            .Where(r => r.WorkflowRunId == workflowRunId && r.TeamId == teamId)
            .OrderBy(r => r.CreatedDate)
            .Select(r => r.EnvelopeJson)
            .ToListAsync(cancellationToken).ConfigureAwait(false))
        .Select(json => JsonSerializer.Deserialize<ReceiptEnvelope>(json, AgentJson.Options)!)
        .ToList();
}
