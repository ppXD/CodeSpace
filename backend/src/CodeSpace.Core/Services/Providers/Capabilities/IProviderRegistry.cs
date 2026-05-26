using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Providers.Capabilities;

/// <summary>
/// Capability-aware resolver. Every consumer asks for exactly the capability surface it
/// needs — there is no "fat provider" abstraction to depend on. Missing capabilities are
/// a first-class state (TryGet) so the UI can drive feature-availability off the registry.
/// </summary>
public interface IProviderRegistry
{
    /// <summary>Throws NotSupportedException if the provider does not implement TCapability.</summary>
    TCapability Require<TCapability>(ProviderKind kind) where TCapability : IProviderCapability;

    /// <summary>Returns false (and a null out) when the provider does not implement TCapability.</summary>
    bool TryGet<TCapability>(ProviderKind kind, out TCapability? capability) where TCapability : class, IProviderCapability;

    /// <summary>All capability interface types implemented by any class registered under this kind. Used by the UI to drive feature availability.</summary>
    IReadOnlyList<Type> GetCapabilities(ProviderKind kind);
}
