using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Providers.Auth;

/// <summary>
/// Resolves a credential row into an ready-to-use auth artifact for one (ProviderKind, AuthType)
/// pair. Adding a new auth type is one new class — provider code never grows.
/// Async by design: GitHub-App installations and OAuth refresh both need a network call to
/// exchange long-lived secrets for short-lived tokens.
/// </summary>
public interface IProviderAuthStrategy
{
    ProviderKind Kind { get; }
    AuthType AuthType { get; }

    Task<ResolvedAuth> ResolveAsync(ProviderContext context, CancellationToken cancellationToken);
}
