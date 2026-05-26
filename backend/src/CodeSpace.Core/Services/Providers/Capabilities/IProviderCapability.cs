using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Providers.Capabilities;

/// <summary>
/// Marker interface for every provider-side capability. Sub-interfaces describe specific
/// abilities (catalog, webhook, signature verification, etc.). A single provider class may
/// implement any subset — consumers resolve only the capability they actually need via
/// <see cref="IProviderRegistry"/>.
/// </summary>
public interface IProviderCapability
{
    ProviderKind Kind { get; }
}
