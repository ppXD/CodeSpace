using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Agents;

/// <summary>What an agent was authored to run on — a credentialed-model ROW, a model name, a credential, or any mix (a persona may supply only a credential). All null means nothing was authored.</summary>
public sealed record ModelPin(Guid? RowId, string? Model, Guid? CredentialId);

/// <summary>One credentialed-model row as the allowed-model-pool choice reads it (Rule 18.1, a pure data noun): its identity, the credential it runs on and that credential's name (for a note a person reads), its provider, whether it can run at all, and the agent-plane ranking inputs.</summary>
public sealed record PooledModelRow
{
    public required Guid Id { get; init; }
    public required string ModelId { get; init; }
    public required Guid ModelCredentialId { get; init; }
    public required string CredentialName { get; init; }
    public required string Provider { get; init; }

    /// <summary>Enabled, under an active, non-deleted credential — a row that is not usable can still be NAMED in a note, never run.</summary>
    public required bool Usable { get; init; }

    public bool IsDefault { get; init; }
    public ModelCapabilityTier? CapabilityTier { get; init; }
    public ModelCapabilityTier? ProbedCapabilityTier { get; init; }
}

/// <summary>The pooled row an authored pin runs on inside an allowed model pool, and — whenever that is not exactly what was authored — why, in a sentence a person reads. A null <see cref="Row"/> means nothing in the pool can run.</summary>
public sealed record PooledModelChoice(PooledModelRow? Row, string? Note);
