using CodeSpace.Api.Extensions.Hangfire;
using CodeSpace.Core.Settings;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Api.Extensions;

/// <summary>
/// Selects the Hangfire registrar for this pod's <see cref="HangfireHosting"/> role. Both roles run the same
/// assembly; the deployment chooses between them with the <c>HangfireHosting</c> configuration key, so the api and
/// worker Deployments are one image pair scaled independently.
/// </summary>
public static class HangfireExtension
{
    public static void AddCodeSpaceHangfire(this IServiceCollection services, IConfiguration configuration) =>
        FindRegistrar(configuration).RegisterHangfire(services, configuration);

    public static void UseCodeSpaceHangfire(this IApplicationBuilder app, IConfiguration configuration) =>
        FindRegistrar(configuration).ApplyHangfire(app, configuration);

    /// <summary>Internal so the role→registrar mapping is unit-pinned without booting a host — an unmapped role would otherwise surface only as a pod that quietly processes nothing.</summary>
    internal static IHangfireRegistrar FindRegistrar(IConfiguration configuration) => ForRole(new HangfireHostingSetting(configuration).Value);

    /// <summary>Pure role→registrar mapping. Exhaustive by construction: a new <see cref="HangfireHosting"/> member fails to compile here rather than silently falling back to a role nobody chose.</summary>
    internal static IHangfireRegistrar ForRole(HangfireHosting hosting) => hosting switch
    {
        HangfireHosting.Api => new ApiHangfireRegistrar(),
        HangfireHosting.Worker => new WorkerHangfireRegistrar(),
        _ => throw new ArgumentOutOfRangeException(nameof(hosting), hosting, "No Hangfire registrar is mapped for this role — a pod started with it would process nothing while looking healthy."),
    };
}
