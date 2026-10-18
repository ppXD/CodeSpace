using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Health;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CodeSpace.Core.Services.Health;

/// <summary>
/// Writes <c>GET /health</c>: this process's build and role, then every check's finding, as JSON. The endpoint is anonymous — a
/// probe cannot sign in — so by default it prints only what an operator needs to compare two images from outside: the build,
/// the role, and each check's name and status. A check's description and data (the git release, the connection topology, the
/// repair backlog) are shown to a signed-in global Admin alone: on an internet-facing API they are reconnaissance. Either way a
/// check that threw is reported by the exception's type, never its message, which can carry an address or a user name.
///
/// <para>Who the caller is comes from <see cref="ICurrentUser"/> — the instance <c>Admin</c> role is a database fact, not a
/// token claim — and a caller whose roles cannot be read is treated as anonymous: this is the endpoint to ask when the
/// database is down, and a signed-in caller must not turn that into an error.</para>
/// </summary>
public static class HealthResponseWriter
{
    private static readonly IReadOnlyDictionary<string, object> NoData = new Dictionary<string, object>();

    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        var posture = context.RequestServices.GetRequiredService<HostPosture>();
        var detailed = IsGlobalAdmin(context.RequestServices);

        var checks = report.Entries.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => ToEntry(entry.Key, entry.Value, detailed)).ToList();

        return context.Response.WriteAsJsonAsync(new HealthResponse(report.Status.ToString(), posture.Build, posture.RoleName, posture.ProcessesJobs, detailed, checks), context.RequestAborted);
    }

    private static bool IsGlobalAdmin(IServiceProvider services)
    {
        try
        {
            return services.GetService<ICurrentUser>()?.HasRole(Roles.Admin) == true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static HealthCheckEntry ToEntry(string name, HealthReportEntry entry, bool detailed)
    {
        if (!detailed) return new HealthCheckEntry(name, entry.Status.ToString(), null, NoData);

        return new HealthCheckEntry(name, entry.Status.ToString(), entry.Exception is null ? entry.Description : entry.Exception.GetType().Name, entry.Data);
    }
}
