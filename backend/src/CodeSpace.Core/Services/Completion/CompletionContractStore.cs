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

    public async Task UpsertRequirementsAsync(Guid workflowRunId, Guid teamId, IReadOnlyList<RequirementEnvelope> requirements, CancellationToken cancellationToken)
    {
        if (requirements.Count == 0) return;

        var refs = requirements.Select(r => r.RequirementRef).ToList();
        var existing = await _db.CompletionRequirement
            .Where(r => r.WorkflowRunId == workflowRunId && r.TeamId == teamId && refs.Contains(r.RequirementRef))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

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
            else if (row.EnvelopeJson != json)
                row.EnvelopeJson = json;   // an amended obligation overwrites its envelope — the ref is the identity
        }

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            // A concurrent producer won the unique index — the obligation exists; upsert semantics hold. Detach
            // the losers so this context's change tracker stays clean for later saves.
            foreach (var entry in _db.ChangeTracker.Entries<CompletionRequirement>().Where(e => e.State == Microsoft.EntityFrameworkCore.EntityState.Added).ToList())
                entry.State = Microsoft.EntityFrameworkCore.EntityState.Detached;
        }
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
