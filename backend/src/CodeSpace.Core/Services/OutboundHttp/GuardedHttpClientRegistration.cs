using CodeSpace.Core.Settings.OutboundHttp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CodeSpace.Core.Services.OutboundHttp;

/// <summary>
/// Registers a named HttpClient whose every connection goes through <see cref="OutboundDestinationGuard"/>, whose every
/// request goes through <see cref="GuardedDestinationHandler"/>, and whose redirects are followed by
/// <see cref="OriginScopedRedirectHandler"/> — the client for outbound HTTP whose URL a workflow author, a trigger
/// payload or an upstream model can write. Extracted so the host (Startup), the integration fixture and the tests all
/// exercise this one registration rather than re-deriving it.
/// </summary>
public static class GuardedHttpClientRegistration
{
    /// <summary>
    /// The host's registration: the operator's committed allowlist, parsed HERE — once, while the host is being built —
    /// so a malformed entry stops the host from starting rather than failing every http.request call at run time; and
    /// the process's proxy (<see cref="HttpClient.DefaultProxy"/>: HTTP(S)_PROXY / NO_PROXY, or the OS setting).
    /// </summary>
    public static IServiceCollection AddGuardedHttpClient(this IServiceCollection services, string name, IConfiguration configuration) =>
        services.AddGuardedHttpClient(name, new OutboundDestinationGuard(new OutboundHttpAllowlistSetting(configuration).Value, HttpClient.DefaultProxy));

    public static IServiceCollection AddGuardedHttpClient(this IServiceCollection services, string name, OutboundDestinationGuard guard)
    {
        services.AddHttpClient(name)
            .ConfigurePrimaryHttpMessageHandler(() => CreatePrimaryHandler(guard))
            .AddHttpMessageHandler(() => new OriginScopedRedirectHandler())
            .AddHttpMessageHandler(() => new GuardedDestinationHandler(guard));

        return services;
    }

    /// <summary>
    /// Redirects off (the handler above follows them). Cookies off: the factory shares one primary handler across every
    /// client it hands out for its lifetime — across runs and teams — so a handler-owned cookie jar would send one run's
    /// session on another's requests; the only cookies sent are the ones an author writes. The guard's proxy, whose
    /// connections the connect callback recognises and dials as the operator's own. HTTP/3 never engages — it bypasses
    /// the connect callback, but requests default to HTTP/1.1 and are never upgraded.
    /// </summary>
    private static SocketsHttpHandler CreatePrimaryHandler(OutboundDestinationGuard guard) => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        Proxy = guard.Proxy,
        ConnectCallback = guard.ConnectAsync,
    };
}
