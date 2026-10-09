using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace CodeSpace.Api.Extensions;

/// <summary>
/// A ceiling on what one source can make the anonymous ingress endpoints do — provider webhooks and workflow callbacks.
/// Both are reached before anything is authenticated, and both used to answer every request without a limit.
///
/// <para>Two limits, in order. First per remote address, across every ingress endpoint: the app's
/// <c>GlobalLimiter</c>, which ASP.NET consults before any endpoint policy and which passes every other endpoint
/// through untouched. Then per (target, address), where the target is the hook id or callback token in the URL. The
/// order is the point. The target is a value the sender picks, and each distinct one gets a limiter of its own that
/// outlives its window, so a sender changing the target on every request was never limited and left a limiter behind
/// each time. Checked first, the address budget runs out, and it bounds how many per-target limiters one sender can
/// create. The routes bound each target's length: a hook id is a GUID, a callback token the 32 characters the engine
/// mints.</para>
///
/// <para>Sized for a provider, not a person. A group / organization hook subscribes to every event — pipeline and job
/// events included — so a busy group sends hundreds a minute to one hook, and a self-managed instance sends every hook's
/// traffic from one address. A provider reads a refusal as a failed delivery: GitLab disables a hook that keeps failing.
/// So the per-hook ceiling sits well above one busy hook, and the per-address one above several of them at once.</para>
///
/// <para>The address is the connection's own; nothing here reads forwarded headers. Behind a proxy that hides the
/// client address, every sender shares one address and so one budget.</para>
/// </summary>
public static class WebhookIngressRateLimitExtension
{
    public const string PolicyName = "webhook-ingress";

    /// <summary>Per (target, address) per window.</summary>
    public const int PermitsPerWindow = 2000;

    /// <summary>Per address per window, across every ingress target — five hooks at their full budget.</summary>
    public const int SourcePermitsPerWindow = 10_000;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>The route values that name what a request is addressed to, in the order the ingress routes declare them.</summary>
    private static readonly string[] TargetRouteValues = { "webhookId", "connectionWebhookId", "token" };

    /// <summary>The one partition every non-ingress request shares in the global limiter — no limit. Never an address.</summary>
    private const string NotIngress = "not-ingress";

    public static void AddWebhookIngressRateLimit(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(SourcePartition);
            options.AddPolicy(PolicyName, context => FixedWindow(PartitionKey(context), PermitsPerWindow));
        });
    }

    /// <summary>The address's budget for an ingress request; no limit for anything else the app serves.</summary>
    private static RateLimitPartition<string> SourcePartition(HttpContext context) =>
        IsIngress(context) ? FixedWindow(SourcePartitionKey(context), SourcePermitsPerWindow) : RateLimitPartition.GetNoLimiter(NotIngress);

    /// <summary>An endpoint that opted into the ingress policy — the same marker the per-target limit is applied by.</summary>
    private static bool IsIngress(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName == PolicyName;

    private static RateLimitPartition<string> FixedWindow(string key, int permits) =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = Window, QueueLimit = 0 });

    /// <summary>Who sent the request, and nothing the sender chose.</summary>
    internal static string SourcePartitionKey(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>"{target}|{address}". A request with no target route value shares one partition per address.</summary>
    internal static string PartitionKey(HttpContext context)
    {
        var target = TargetRouteValues.Select(name => context.GetRouteValue(name)?.ToString()).FirstOrDefault(value => !string.IsNullOrEmpty(value)) ?? "-";

        return $"{target}|{SourcePartitionKey(context)}";
    }
}
