using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Contracts;

namespace CodeSpace.Core.Services.Completion;

public interface ICompletionCapabilityRegistry
{
    /// <summary>The descriptor for a capability key, or null for an UNKNOWN capability — the caller must read null as Unsupported, never as a license to attempt (Lock Clause 4).</summary>
    CapabilityDescriptor? Resolve(string capabilityKey);

    /// <summary>Every registered capability key — the claim board iterates the closed vocabulary.</summary>
    IReadOnlyCollection<string> RegisteredKeys { get; }
}

/// <summary>
/// P2b-3: the CLOSED capability vocabulary with committed qualification statuses (no deployment toggles — a
/// status change is a reviewed edit to this table). Today's honest states: the git surfaces are accumulating
/// shadow evidence; the inline answer is still open development (its verifier — "the answer itself is the
/// deliverable" — has no oracle floor yet). A key outside this table resolves null → the terminal authority
/// reads Unsupported, so a novel ask (image, spreadsheet, external effect) parks honestly instead of
/// terminalizing a fake Success. Readiness is pinned by test; measured performance is deliberately NOT a column
/// here — it resolves from the qualification-receipt ledger at read time (Q4's claim gate), so a Sealed statement
/// is impossible without a current sealed receipt.
/// </summary>
public sealed class CompletionCapabilityRegistry : ICompletionCapabilityRegistry, ISingletonDependency
{
    private static readonly IReadOnlyDictionary<string, CapabilityDescriptor> Registered = new[]
    {
        new CapabilityDescriptor { Key = CapabilityKeys.GitBranch, Readiness = ProtocolReadiness.Shadow },
        new CapabilityDescriptor { Key = CapabilityKeys.GitPatch, Readiness = ProtocolReadiness.Shadow },
        new CapabilityDescriptor { Key = CapabilityKeys.InlineAnswer, Readiness = ProtocolReadiness.Open },
    }.ToDictionary(d => d.Key, StringComparer.Ordinal);

    public CapabilityDescriptor? Resolve(string capabilityKey) => Registered.GetValueOrDefault(capabilityKey);

    public IReadOnlyCollection<string> RegisteredKeys => Registered.Keys.ToArray();
}

/// <summary>The v1 derivation of WHAT a run was asked for, read off its own staked obligation set — a pure function so the mapping pins without a database.</summary>
public static class CompletionCapability
{
    /// <summary>Delivery Required → the branch surface; output Required without delivery → the patch surface; neither → the answer itself. Authorized-NA stakes are explicit "not asked" and never select a surface.</summary>
    public static string Derive(IReadOnlyList<RequirementEnvelope> requirements)
    {
        if (HasRequired(requirements, ContractKinds.Delivery)) return CapabilityKeys.GitBranch;
        if (HasRequired(requirements, ContractKinds.Output)) return CapabilityKeys.GitPatch;
        return CapabilityKeys.InlineAnswer;
    }

    private static bool HasRequired(IReadOnlyList<RequirementEnvelope> requirements, string kind) =>
        requirements.Any(r => r.Kind == kind && r.Requiredness == Requiredness.Required);
}
