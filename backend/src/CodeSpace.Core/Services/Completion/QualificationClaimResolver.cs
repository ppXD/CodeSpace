using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Contracts;
using System.Text.Json;

namespace CodeSpace.Core.Services.Completion;

public interface IQualificationClaimResolver
{
    /// <summary>The pair's measured standing at <paramref name="asOf"/> — resolved from the receipt ledger, never from a constant.</summary>
    Task<PerformanceClaim> ResolveAsync(string mode, string capabilityKey, DateTimeOffset asOf, CancellationToken cancellationToken);

    /// <summary>Every registered (mode × capability) pair's standing at <paramref name="asOf"/> — the board a UI renders. The unregistered generic mode is deliberately absent.</summary>
    Task<QualificationClaimBoard> ResolveBoardAsync(DateTimeOffset asOf, CancellationToken cancellationToken);
}

/// <summary>
/// Q4 (SOTA-claim gate): measured performance has exactly ONE lawful source — the immutable qualification-receipt
/// ledger. The committed registries declare protocol READINESS (a reviewed decision) and deliberately carry no
/// performance column: "measured" as a constant is the laundering this gate outlaws. Sealed resolves only off a
/// CURRENT sealed receipt and carries its identity; expiry and revocation downgrade the claim at read time with
/// no code change.
/// </summary>
public sealed class QualificationClaimResolver : IQualificationClaimResolver, IScopedDependency
{
    private readonly IQualificationReceiptStore _receipts;
    private readonly IModeProfileRegistry _modes;
    private readonly ICompletionCapabilityRegistry _capabilities;

    public QualificationClaimResolver(IQualificationReceiptStore receipts, IModeProfileRegistry modes, ICompletionCapabilityRegistry capabilities)
    {
        _receipts = receipts;
        _modes = modes;
        _capabilities = capabilities;
    }

    public async Task<PerformanceClaim> ResolveAsync(string mode, string capabilityKey, DateTimeOffset asOf, CancellationToken cancellationToken) =>
        Fold(mode, capabilityKey, await _receipts.ListCurrentAsync(mode, capabilityKey, asOf, cancellationToken).ConfigureAwait(false));

    public async Task<QualificationClaimBoard> ResolveBoardAsync(DateTimeOffset asOf, CancellationToken cancellationToken)
    {
        var rows = new List<PerformanceClaim>();

        foreach (var mode in _modes.RegisteredModes.OrderBy(m => m, StringComparer.Ordinal))
        foreach (var capability in _capabilities.RegisteredKeys.OrderBy(k => k, StringComparer.Ordinal))
            rows.Add(await ResolveAsync(mode, capability, asOf, cancellationToken).ConfigureAwait(false));

        return new QualificationClaimBoard { AsOf = asOf, Rows = rows };
    }

    /// <summary>The claim fold: the highest-granting CURRENT receipt backs the claim VERBATIM (Sealed outranks Shadow even when the shadow round is newer; equal grants go to the latest round); no current receipt = Unmeasured, backed by nothing. A claim can never exceed what its backing receipt granted.</summary>
    internal static PerformanceClaim Fold(string mode, string capabilityKey, IReadOnlyList<QualificationReceipt> current)
    {
        var backing = current.OrderByDescending(r => r.GrantedPerformance).ThenByDescending(r => r.EffectiveFrom).FirstOrDefault();

        return new PerformanceClaim
        {
            Mode = mode,
            CapabilityKey = capabilityKey,
            Performance = backing?.GrantedPerformance ?? PerformanceQualification.Unmeasured,
            ReceiptId = backing?.Id,
            SuiteDigest = backing?.SuiteDigest,
            ExpiresAt = backing?.ExpiresAt,
            Cohort = backing is null ? null : Parse<LaunchCohortDescriptor>(backing.CohortJson),
            Seal = backing is null ? null : new ContractSeal
            {
                CapabilityKey = backing.CapabilityKey,
                SuiteDigest = backing.SuiteDigest,
                VerifierBundle = Parse<VerifierBundle>(backing.CohortJson),
            },
        };
    }

    /// <summary>Legacy ad-hoc json (or a shape missing the noun's required keys) reads NULL — a partial identity is no identity, never a half-filled record.</summary>
    private static T? Parse<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try { return JsonSerializer.Deserialize<T>(json, Agents.AgentJson.Options); }
        catch (JsonException) { return null; }
    }
}
