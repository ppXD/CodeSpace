using CodeSpace.Core.Settings.Application;
using Microsoft.AspNetCore.Routing.Patterns;
using Serilog.Context;
using Serilog.Core;
using Serilog.Events;

namespace CodeSpace.Api.Logging;

/// <summary>
/// Keeps bearer tokens that travel in a URL PATH out of the logs. Invitation, password-reset and workflow-callback links
/// carry their token as a path segment (the URL shapes are issued and cannot change), and the request path is on every
/// line a request logs: in Serilog's completion line, and as <c>RequestPath</c> from the ASP.NET hosting scope on every
/// event logged while the request runs. Whoever could read the console or Seq could otherwise join a team, take over an
/// account or resume a run.
///
/// <para>For a matched route that binds a bearer parameter, both carry the route's TEMPLATE instead
/// (<c>/api/invitations/{token}/accept</c>), which still says which endpoint ran. Keyed on the parameter's NAME
/// (<see cref="BearerParameters"/>) rather than a list of paths, so a new token route is covered by naming its parameter
/// <c>token</c>; an E2E test fails if a route binds a credential-looking parameter this does not recognise.</para>
///
/// <para>The same token also arrives where no bearer route answers: a GET or HEAD on a callback URL (a link unfurler, a
/// person pasting it into a browser, an uptime ping), a wrong method on an invitation route, the SPA's own
/// <c>/invite/…</c> or <c>/reset-password/…</c> link opened on the API origin, and every line logged before routing has
/// chosen an endpoint (UseHttpsRedirection warns once per process without an https port). Those requests are answered by
/// the fallback, a method rejection or nothing yet, so the path's SHAPE decides: a path whose leading segments have the
/// shape of a bearer route up to its token is logged with that segment as <c>{token}</c>
/// (<c>/api/workflows/callbacks/{token}</c>). The shapes are the bearer routes in the host's endpoint table plus
/// <see cref="SpaBearerRoutes"/>. A request a non-bearer route answered logs exactly what it did before.</para>
///
/// <para>What this cannot reach: lines the server writes outside the pipeline — ASP.NET's "Request starting …" before
/// this middleware runs, and its line for an exception that escapes every middleware (an action's exception never does;
/// the global filter handles it). The first is Information and the second Error under <c>Microsoft.AspNetCore</c>; the
/// shipped <c>Serilog:MinimumLevel:Override:Microsoft = Warning</c> keeps the first out, and a test pins that setting.</para>
/// </summary>
public static class BearerRouteLogging
{
    /// <summary>Route parameter names whose value is a bearer credential.</summary>
    public static readonly IReadOnlySet<string> BearerParameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "token" };

    /// <summary>
    /// The SPA's token routes — the links <see cref="PublicBaseUrlSetting"/> mints, and the prefixes the SPA's nginx masks.
    /// This server never serves them, so no endpoint names them, but a link built against the API origin brings them here.
    /// </summary>
    public static readonly IReadOnlyList<string> SpaBearerRoutes = [PublicBaseUrlSetting.InvitePath + "{token}", PublicBaseUrlSetting.PasswordResetPath + "{token}"];

    private const string RequestPathProperty = "RequestPath";

    /// <summary>The path to log for this request: a bearer route's template, else the path with any bearer segment masked, else the raw path.</summary>
    public static string PathOf(HttpContext context) => TemplateOf(context) ?? context.Request.Path.Value ?? string.Empty;

    /// <summary>
    /// <c>RequestLoggingOptions.GetMessageTemplateProperties</c>: the same four properties Serilog's default completion line
    /// carries, with the bearer-safe path in place of a token's. <paramref name="requestPath"/> is what Serilog would log.
    /// </summary>
    public static IEnumerable<LogEventProperty> RequestCompletionProperties(HttpContext context, string requestPath, double elapsedMs, int statusCode) =>
    [
        new("RequestMethod", new ScalarValue(context.Request.Method)),
        new(RequestPathProperty, new ScalarValue(TemplateOf(context) ?? requestPath)),
        new("StatusCode", new ScalarValue(statusCode)),
        new("Elapsed", new ScalarValue(elapsedMs)),
    ];

    /// <summary>
    /// Middleware, FIRST in the pipeline: every event the request logs carries the bearer-safe path as <c>RequestPath</c> —
    /// the completion line, everything downstream of routing, what routing itself logs the moment it matches, and what the
    /// middleware ahead of routing logs. The hosting scope's raw value reaches an event through Serilog's logging provider,
    /// which only adds a property that is absent; this enricher overwrites, so it wins in either order.
    /// </summary>
    public static IApplicationBuilder UseBearerRouteLogging(this IApplicationBuilder app)
    {
        var shapes = new BearerPathShapes(app.ApplicationServices.GetService<EndpointDataSource>());

        return app.Use((context, next) => RewriteRequestPathAsync(context, next, shapes));
    }

    private static async Task RewriteRequestPathAsync(HttpContext context, RequestDelegate next, BearerPathShapes shapes)
    {
        context.Features.Set(shapes.Loaded());
        var route = new BearerRouteEnricher(context);

        try
        {
            using (LogContext.Push(route))
                await next(context).ConfigureAwait(false);
        }
        finally
        {
            route.Settle();
        }
    }

    /// <summary>
    /// A bearer route's template when one answered; null when any other route did, its literals accounting for the path;
    /// otherwise — no endpoint yet, the catch-all fallback, a method rejection — the path with a bearer segment masked.
    /// </summary>
    private static string? TemplateOf(HttpContext context) => context.GetEndpoint() switch
    {
        RouteEndpoint endpoint when IsBearerRoute(endpoint.RoutePattern) => context.Request.PathBase.Value + "/" + endpoint.RoutePattern.RawText?.TrimStart('/'),
        RouteEndpoint endpoint when !endpoint.RoutePattern.Parameters.Any(parameter => parameter.IsCatchAll) => null,
        _ => context.Features.Get<BearerPathShapes>()?.Mask(context.Request.Path.Value) is { } masked ? context.Request.PathBase.Value + masked : null,
    };

    private static bool IsBearerRoute(RoutePattern pattern) => pattern.Parameters.Any(parameter => BearerParameters.Contains(parameter.Name));

    /// <summary>
    /// Every bearer route's shape, read once from the host's endpoint table on the first request (the table is complete only
    /// once the host has started) — in the middleware, so a failure to read it fails that request rather than leaving a log
    /// line unmasked.
    /// </summary>
    private sealed class BearerPathShapes
    {
        private readonly Lazy<IReadOnlyList<RoutePattern>> _routes;

        public BearerPathShapes(EndpointDataSource? endpoints) => _routes = new(() => BearerRoutesOf(endpoints));

        public BearerPathShapes Loaded()
        {
            _ = _routes.Value;

            return this;
        }

        /// <summary>The path with every segment a bearer route's token would bind replaced by <c>{name}</c>; null when no bearer route has its shape.</summary>
        public string? Mask(string? path)
        {
            var segments = (path ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
            var bearers = _routes.Value.Select(route => BearerSegmentOf(route, segments)).OfType<(int Index, string Name)>().ToList();

            if (bearers.Count == 0) return null;

            foreach (var (index, name) in bearers)
                segments[index] = "{" + name + "}";

            return "/" + string.Join('/', segments);
        }

        private static IReadOnlyList<RoutePattern> BearerRoutesOf(EndpointDataSource? endpoints) =>
            [.. SpaBearerRoutes.Select(RoutePatternFactory.Parse), .. endpoints?.Endpoints.OfType<RouteEndpoint>().Select(endpoint => endpoint.RoutePattern).Where(IsBearerRoute) ?? []];

        /// <summary>
        /// Walks the route's segments against the path's, case-insensitively as routing matches: a literal must equal its
        /// segment, any other part matches anything, and reaching the bearer parameter names the segment it would bind. HTTP
        /// method and constraints are ignored — a token in the path is a token whichever route would have refused it.
        /// </summary>
        private static (int Index, string Name)? BearerSegmentOf(RoutePattern route, string[] segments)
        {
            for (var i = 0; i < route.PathSegments.Count && i < segments.Length; i++)
            {
                var parts = route.PathSegments[i].Parts;

                if (parts is [RoutePatternParameterPart bearer] && BearerParameters.Contains(bearer.Name)) return (i, bearer.Name);

                if (parts is [RoutePatternLiteralPart literal] && !string.Equals(literal.Content, segments[i], StringComparison.OrdinalIgnoreCase)) return null;
            }

            return null;
        }
    }

    /// <summary>
    /// The request's bearer-safe path, read live while the request runs (routing matches it part-way through) and kept once
    /// it ends, for work the request started that still logs — the server recycles the <see cref="HttpContext"/>, so it is
    /// never read after that.
    /// </summary>
    private sealed class BearerRouteEnricher : ILogEventEnricher
    {
        private volatile HttpContext? _request;
        private volatile string? _settledTemplate;

        public BearerRouteEnricher(HttpContext request) { _request = request; }

        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
        {
            var template = _request is { } request ? TemplateOf(request) : _settledTemplate;

            if (template != null) logEvent.AddOrUpdateProperty(propertyFactory.CreateProperty(RequestPathProperty, template));
        }

        public void Settle()
        {
            _settledTemplate = _request is { } request ? TemplateOf(request) : null;
            _request = null;
        }
    }
}
