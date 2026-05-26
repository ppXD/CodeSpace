using CodeSpace.Messages.Constants;
using Hangfire.Dashboard;

namespace CodeSpace.Api.Extensions.Hangfire;

/// <summary>
/// Gate the <c>/hangfire</c> dashboard behind the Admin role. Hangfire's dashboard exposes
/// job retry/delete buttons — an authenticated non-admin operator shouldn't be able to
/// mutate background job state, and an unauthenticated visitor shouldn't see anything at all.
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
