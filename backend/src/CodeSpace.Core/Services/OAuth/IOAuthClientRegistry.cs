using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.OAuth;

public interface IOAuthClientRegistry
{
    IOAuthClient Get(ProviderKind kind);
}
