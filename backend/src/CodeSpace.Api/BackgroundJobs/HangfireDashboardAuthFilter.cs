using CodeSpace.Messages.Constants;
using Hangfire.Dashboard;

namespace CodeSpace.Api.BackgroundJobs;

/// <summary>
/// Gate the <c>/hangfire</c> dashboard behind the Admin role. Hangfire's dashboard
/// exposes job retry/delete buttons — an authenticated non-admin operator shouldn't be
/// able to mutate background job state, and an unauthenticated visitor shouldn't see
/// anything at all.
///
/// <para>Hangfire's <c>IDashboardAuthorizationFilter</c> contract takes the raw
/// <c>DashboardContext</c>; we resolve the standard ASP.NET <c>HttpContext.User</c> and
/// check the Admin role claim. Failing the filter returns 403 with no body — same shape
/// as the rest of the API surface's authorization.</para>
/// </summary>
public sealed class HangfireDashboardAuthFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();
        var user = httpContext.User;

        if (user.Identity?.IsAuthenticated != true) return false;

        return user.IsInRole(Roles.Admin);
    }
}
