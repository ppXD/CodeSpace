using CodeSpace.Core.Persistence.Entities;

namespace CodeSpace.Core.Services.Providers;

public sealed record ProviderContext(ProviderInstance Instance, Credential Credential);
