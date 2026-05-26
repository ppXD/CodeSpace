using CodeSpace.Core.Services.Providers.Modules;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Providers;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Providers;

/// <summary>
/// Surfaces the provider module's hard-coded defaults so the frontend doesn't duplicate them.
/// Renaming a default scope = one edit in the provider module, frontend picks it up on
/// next render. No DB; reads the in-memory catalog.
/// </summary>
public sealed class GetProviderDefaultsQueryHandler : IRequestHandler<GetProviderDefaultsQuery, ProviderDefaults>
{
    private readonly IProviderModuleCatalog _modules;
    private readonly CodeSpace.Core.Settings.OAuth.OAuthCallbackUrlSetting _callbackUrlSetting;

    public GetProviderDefaultsQueryHandler(IProviderModuleCatalog modules, CodeSpace.Core.Settings.OAuth.OAuthCallbackUrlSetting callbackUrlSetting)
    {
        _modules = modules;
        _callbackUrlSetting = callbackUrlSetting;
    }

    public Task<ProviderDefaults> Handle(GetProviderDefaultsQuery request, CancellationToken cancellationToken)
    {
        var module = _modules.Get(request.Provider) ?? throw new KeyNotFoundException($"No provider module registered for kind {request.Provider}");

        return Task.FromResult(new ProviderDefaults
        {
            Provider = request.Provider,
            DefaultBaseUrl = DefaultBaseUrlFor(request.Provider),
            DefaultDisplayName = DefaultDisplayNameFor(request.Provider),
            DefaultOAuthScopes = module.DefaultOAuthScopes,
            OAuthCallbackUrl = _callbackUrlSetting.Value ?? string.Empty
        });
    }

    private static string DefaultBaseUrlFor(ProviderKind kind) => kind switch
    {
        ProviderKind.GitHub => "https://github.com",
        ProviderKind.GitLab => "https://gitlab.com",
        _ => string.Empty
    };

    private static string DefaultDisplayNameFor(ProviderKind kind) => kind switch
    {
        ProviderKind.GitHub => "GitHub",
        ProviderKind.GitLab => "GitLab",
        ProviderKind.Git => "Git",
        _ => kind.ToString()
    };
}
