using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Providers.Errors;

public interface IProviderErrorMapperRegistry
{
    IProviderErrorMapper? Get(ProviderKind kind);
}
