using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Providers.Modules;

/// <summary>
/// Runtime registry of every loaded IProviderModule. Drives the /api/providers discovery
/// endpoint and lets tests assert which providers are wired in.
/// </summary>
public interface IProviderModuleCatalog
{
    IReadOnlyList<IProviderModule> Modules { get; }

    IProviderModule? Get(ProviderKind kind);
}
