using System.Collections.Concurrent;
using CodeSpace.Api.Controllers;
using CodeSpace.Api.Logging;
using CodeSpace.Core.Settings.Application;
using CodeSpace.Core.Services.Workflows.Callbacks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Shouldly;

namespace CodeSpace.IntegrationTests.Logging;

/// <summary>
/// Invitation, password-reset and callback tokens travel in URL PATHS, and the request path is on every log line of the
/// request: in Serilog's completion line, and as <c>RequestPath</c> from the ASP.NET hosting scope on every event logged
/// while the request runs. For a route that binds a bearer parameter the path logged is the route's template instead.
/// These pin the template rewrite, its precedence over the hosting scope's property, and the operator-level setting the
/// one line it cannot reach depends on. Pure — they live here because UnitTests has no CodeSpace.Api reference.
/// </summary>
[Trait("Category", "Integration")]
public sealed class BearerRouteLoggingTests
{
    private const string Canary = "CANARY-bearer-route-7c4e1f9a2b";

    [Theory]
    [InlineData("api/invitations/{token}", "/api/invitations/" + Canary, "/api/invitations/{token}")]
    [InlineData("api/invitations/{token}/accept", "/api/invitations/" + Canary + "/accept", "/api/invitations/{token}/accept")]
    [InlineData("api/auth/reset-password/{token}", "/api/auth/reset-password/" + Canary, "/api/auth/reset-password/{token}")]
    [InlineData("api/workflows/callbacks/{token:length(32)}", "/api/workflows/callbacks/" + Canary, "/api/workflows/callbacks/{token:length(32)}")]
    [InlineData("api/workflows/runs/{runId:guid}", "/api/workflows/runs/0b5f2c1e-0000-4000-8000-000000000001", "/api/workflows/runs/0b5f2c1e-0000-4000-8000-000000000001")]
    public void The_logged_path_is_the_route_template_only_where_the_route_binds_a_bearer_parameter(string template, string requestPath, string expected)
    {
        BearerRouteLogging.PathOf(RoutedContext(template, requestPath)).ShouldBe(expected);
    }

    [Fact]
    public void An_unrouted_request_logs_its_raw_path()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/no/such/route";

        BearerRouteLogging.PathOf(context).ShouldBe("/no/such/route");
    }

    [Theory]
    [InlineData(Unrouted, "/api/workflows/callbacks/" + Canary, "/api/workflows/callbacks/{token}")]                // before routing: UseHttpsRedirection's port warning
    [InlineData(Fallback, "/api/workflows/callbacks/" + Canary, "/api/workflows/callbacks/{token}")]                // GET or HEAD on a revealed callback URL
    [InlineData(Fallback, "/API/Workflows/Callbacks/" + Canary + "/", "/API/Workflows/Callbacks/{token}")]          // routing ignores case and a trailing slash
    [InlineData(Fallback, "/api/invitations/" + Canary + "/accept", "/api/invitations/{token}/accept")]             // GET on the accept route
    [InlineData(Fallback, "/api/invitations/" + Canary + "/accept/more", "/api/invitations/{token}/accept/more")]   // a path longer than any route
    [InlineData(Fallback, "/invite/" + Canary, "/invite/{token}")]                                                   // the SPA's invite link opened on the API origin
    [InlineData(Fallback, "/reset-password/" + Canary, "/reset-password/{token}")]                                   // the SPA's reset link opened on the API origin
    [InlineData(Rejected, "/api/auth/reset-password/" + Canary, "/api/auth/reset-password/{token}")]                // a 405 rejection endpoint
    [InlineData(Fallback, "/api/workflows/runs/" + Canary, "/api/workflows/runs/" + Canary)]                         // no bearer route has this shape
    [InlineData("api/invitations/mine", "/api/invitations/mine", "/api/invitations/mine")]                             // a route that is not a bearer route answered it
    public async Task A_request_no_bearer_route_answered_is_logged_with_its_bearer_path_segment_masked(string answeredBy, string requestPath, string expected)
    {
        // Redaction used to key only on the endpoint that answered. The same token reaches the fallback on a GET, a HEAD or
        // the SPA's path, and reaches the log before routing has matched anything — so the path's SHAPE decides too.
        var (sink, logger, app) = Pipeline();
        string? logged = null;
        app.Run(context =>
        {
            context.SetEndpoint(Answering(answeredBy));
            logger.LogWarning("after the endpoint was chosen");
            logged = BearerRouteLogging.PathOf(context);
            return Task.CompletedTask;
        });

        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = requestPath;
        using (logger.BeginScope(new Dictionary<string, object> { ["RequestPath"] = requestPath }))
            await app.Build()(context);

        logged.ShouldBe(expected);
        sink.Events.Single().Properties["RequestPath"].ToString().ShouldBe($"\"{expected}\"", "the hosting scope's raw path on every event the request logs");
        if (expected != requestPath) sink.Rendered.ShouldNotContain(Canary);
    }

    [Fact]
    public async Task Every_bearer_route_whose_shape_the_path_has_masks_its_own_segment()
    {
        // Shapes are matched against the path as requested, not as an earlier shape masked it: a shorter route masking the
        // segment a longer route expects as a literal must not hide the longer route's token.
        var (_, _, app) = Pipeline(BearerEndpoint("hooks/{token}"), BearerEndpoint("hooks/inbound/{token}"));
        string? logged = null;
        app.Run(context =>
        {
            logged = BearerRouteLogging.PathOf(context);
            return Task.CompletedTask;
        });

        var context = new DefaultHttpContext();
        context.Request.Path = "/hooks/inbound/" + Canary;
        await app.Build()(context);

        logged.ShouldBe("/hooks/{token}/{token}");
    }

    [Fact]
    public async Task An_event_logged_before_routing_carries_the_masked_path()
    {
        // UseHttpsRedirection runs before UseRouting and warns once per process when it cannot find the https port; the
        // first request of a fresh non-Development pod carried its path there with no endpoint chosen yet.
        var (sink, logger, app) = Pipeline();
        app.Use(async (context, next) =>
        {
            logger.LogWarning("Failed to determine the https port for redirect.");
            await next(context);
        });
        app.Run(_ => Task.CompletedTask);

        var context = new DefaultHttpContext();
        context.Request.Path = "/api/workflows/callbacks/" + Canary;
        using (logger.BeginScope(new Dictionary<string, object> { ["RequestPath"] = context.Request.Path.Value! }))
            await app.Build()(context);

        sink.Events.Single().Properties["RequestPath"].ToString().ShouldBe("\"/api/workflows/callbacks/{token}\"");
        sink.Rendered.ShouldNotContain(Canary);
    }

    [Fact]
    public void SpaBearerRoutes_are_the_paths_the_links_are_minted_on()
    {
        // The SPA's token routes are not this server's, so no endpoint names them; they are the prefixes PublicBaseUrlSetting
        // mints links on (and the ones nginx masks).
        var minted = new PublicBaseUrlSetting(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [PublicBaseUrlSetting.ConfigurationKey] = "https://spa.example" }).Build());

        BearerRouteLogging.SpaBearerRoutes.ShouldBe(["/invite/{token}", "/reset-password/{token}"]);
        minted.InviteUrl(Canary).ShouldBe("https://spa.example/invite/" + Canary);
        minted.PasswordResetUrl(Canary).ShouldBe("https://spa.example/reset-password/" + Canary);
    }

    [Fact]
    public void The_request_completion_line_carries_the_template_and_keeps_the_raw_path_elsewhere()
    {
        var bearer = BearerRouteLogging.RequestCompletionProperties(RoutedContext("api/invitations/{token}", "/api/invitations/" + Canary), "/api/invitations/" + Canary, 12.5, 200).ToDictionary(p => p.Name, p => p.Value.ToString());
        var plain = BearerRouteLogging.RequestCompletionProperties(RoutedContext("api/workflows/runs", "/api/workflows/runs"), "/api/workflows/runs", 1, 204).ToDictionary(p => p.Name, p => p.Value.ToString());

        bearer["RequestPath"].ShouldBe("\"/api/invitations/{token}\"");
        bearer.Keys.ShouldBe(["RequestMethod", "RequestPath", "StatusCode", "Elapsed"], ignoreOrder: true, "the same four properties Serilog's default completion line carries, so the message template keeps rendering");
        plain["RequestPath"].ShouldBe("\"/api/workflows/runs\"", "every other route logs the path Serilog handed in, unchanged");
        string.Concat(bearer.Values).ShouldNotContain(Canary);
    }

    [Theory]
    [InlineData("api/invitations/{token}/accept", "/api/invitations/" + Canary + "/accept", "/api/invitations/{token}/accept")]
    [InlineData("api/workflows/runs/{runId:guid}", "/api/workflows/runs/0b5f2c1e-0000-4000-8000-000000000001", "/api/workflows/runs/0b5f2c1e-0000-4000-8000-000000000001")]
    public async Task An_event_inside_the_endpoint_carries_the_template_over_the_hosting_scopes_raw_path_only_on_a_bearer_route(string template, string requestPath, string expected)
    {
        // The hosting scope's RequestPath reaches an event through Serilog's MEL provider, which adds scope properties only
        // when absent. The middleware's LogContext enricher overwrites, so it wins whichever runs first.
        var (sink, logger, app) = Pipeline();
        app.Run(_ =>
        {
            logger.LogInformation("inside the endpoint");
            return Task.CompletedTask;
        });

        var context = RoutedContext(template, requestPath);
        using (logger.BeginScope(new Dictionary<string, object> { ["RequestPath"] = context.Request.Path.Value!, ["RequestId"] = "r-1" }))
            await app.Build()(context);

        var logged = sink.Events.Single();
        logged.Properties["RequestPath"].ToString().ShouldBe($"\"{expected}\"");
        logged.Properties["RequestId"].ToString().ShouldBe("\"r-1\"", "the rest of the hosting scope is untouched");
        sink.Rendered.ShouldNotContain(Canary);
    }

    [Fact]
    public async Task An_event_logged_the_moment_routing_matches_carries_the_template()
    {
        // Routing logs as it selects the endpoint (a body-size warning, its Debug "matched" line) — after the hosting scope
        // was begun, before any middleware placed after UseRouting runs. Here the stand-in for routing sets the endpoint and
        // logs straight away.
        var (sink, logger, app) = Pipeline();
        app.Use(async (context, next) =>
        {
            context.SetEndpoint(BearerEndpoint("api/workflows/callbacks/{token:length(32)}"));
            logger.LogWarning("routing matched");
            await next(context);
        });
        app.Run(_ => Task.CompletedTask);

        var context = new DefaultHttpContext();
        context.Request.Path = "/api/workflows/callbacks/" + Canary;
        using (logger.BeginScope(new Dictionary<string, object> { ["RequestPath"] = context.Request.Path.Value! }))
            await app.Build()(context);

        sink.Events.Single().Properties["RequestPath"].ToString().ShouldBe("\"/api/workflows/callbacks/{token:length(32)}\"");
    }

    [Fact]
    public async Task Work_the_request_started_that_logs_after_it_ended_still_carries_the_template()
    {
        var (sink, logger, app) = Pipeline();
        var release = new TaskCompletionSource();
        Task? straggler = null;
        app.Run(_ =>
        {
            straggler = Task.Run(async () =>
            {
                await release.Task;
                logger.LogInformation("after the response");
            });
            return Task.CompletedTask;
        });

        var context = RoutedContext("api/auth/reset-password/{token}", "/api/auth/reset-password/" + Canary);
        using (logger.BeginScope(new Dictionary<string, object> { ["RequestPath"] = context.Request.Path.Value! }))
            await app.Build()(context);

        context.SetEndpoint(null);   // the server recycles the context once the request is over
        release.SetResult();
        await straggler!;

        sink.Events.Single().Properties["RequestPath"].ToString().ShouldBe("\"/api/auth/reset-password/{token}\"");
    }

    [Fact]
    public void BearerParameters_is_pinned()
    {
        // Keyed on the route parameter NAME, so a new token route is covered by naming its parameter "token".
        BearerRouteLogging.BearerParameters.ShouldBe(["token"]);
    }

    [Fact]
    public void The_callback_url_the_reveal_hands_out_is_the_path_the_callback_route_serves()
    {
        var controllerRoute = typeof(WorkflowCallbacksController).GetCustomAttributes(typeof(RouteAttribute), inherit: false).Cast<RouteAttribute>().Single().Template;
        var actionRoute = typeof(WorkflowCallbacksController).GetMethod(nameof(WorkflowCallbacksController.Resume))!.GetCustomAttributes(typeof(HttpPostAttribute), inherit: false).Cast<HttpPostAttribute>().Single().Template;

        WorkflowCallbackUrlService.RoutePrefix.ShouldBe("/" + controllerRoute + "/");
        actionRoute.ShouldBe("{token:length(32)}", "the reveal appends the bare token, and the route binds it as 'token' — the name the log redaction keys on");
    }

    [Fact]
    public void The_shipped_levels_keep_the_pre_routing_request_line_out_of_the_log()
    {
        // ASP.NET's own "Request starting POST https://host/api/invitations/<token>" is logged before routing, so no route
        // template exists to put in its place. It is Information under Microsoft.AspNetCore.Hosting.Diagnostics; the shipped
        // override keeps it out. Lowering it would print every bearer token in a path — make that a reviewed decision.
        var shipped = new ConfigurationBuilder().AddJsonFile(Path.Combine(FindBackendRoot(), "src", "CodeSpace.Api", "appsettings.json")).Build();
        var overrides = shipped.GetSection("Serilog:MinimumLevel:Override").GetChildren().ToDictionary(c => c.Key, c => c.Value);

        overrides["Microsoft"].ShouldBe("Warning");
        overrides.Keys.Where(k => k.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)).ShouldBeEmpty("a narrower override could re-enable the hosting request line");
    }

    private static DefaultHttpContext RoutedContext(string template, string requestPath)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = requestPath;
        context.SetEndpoint(BearerEndpoint(template));

        return context;
    }

    private static RouteEndpoint BearerEndpoint(string template) => new(_ => Task.CompletedTask, RoutePatternFactory.Parse(template), 0, EndpointMetadataCollection.Empty, template);

    private const string Unrouted = "(nothing yet)";
    private const string Fallback = "{*path:nonfile}";
    private const string Rejected = "(405)";

    /// <summary>The endpoint routing chose: none yet, MapFallback's catch-all, the method-rejection endpoint (not a RouteEndpoint), or a route by its template.</summary>
    private static Endpoint? Answering(string answeredBy) => answeredBy switch
    {
        Unrouted => null,
        Rejected => new Endpoint(_ => Task.CompletedTask, EndpointMetadataCollection.Empty, "405 HTTP Method Not Supported"),
        _ => new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse(answeredBy), answeredBy == Fallback ? int.MaxValue : 0, EndpointMetadataCollection.Empty, answeredBy),
    };

    /// <summary>The API's bearer routes as the host maps them, plus a route that is not one.</summary>
    private static readonly Endpoint[] HostEndpoints =
    [
        BearerEndpoint("api/invitations/{token}"),
        BearerEndpoint("api/invitations/{token}/accept"),
        BearerEndpoint("api/invitations/mine"),
        BearerEndpoint("api/auth/reset-password/{token}"),
        BearerEndpoint("api/workflows/callbacks/{token:length(32)}"),
        BearerEndpoint("api/workflows/runs/{runId:guid}"),
    ];

    /// <summary>The redaction middleware in front of whatever the test adds, logging through Serilog's MEL provider the way the host does, over the host's endpoint table.</summary>
    private static (CapturedEvents Sink, Microsoft.Extensions.Logging.ILogger Logger, ApplicationBuilder App) Pipeline() => Pipeline(HostEndpoints);

    private static (CapturedEvents Sink, Microsoft.Extensions.Logging.ILogger Logger, ApplicationBuilder App) Pipeline(params Endpoint[] hostEndpoints)
    {
        var sink = new CapturedEvents();
        var serilog = new LoggerConfiguration().MinimumLevel.Verbose().Enrich.FromLogContext().WriteTo.Sink(sink).CreateLogger();
        var logger = new SerilogLoggerFactory(serilog, dispose: true).CreateLogger("Request");
        var services = new ServiceCollection().AddSingleton<EndpointDataSource>(new DefaultEndpointDataSource(hostEndpoints)).BuildServiceProvider();
        var app = new ApplicationBuilder(services);
        app.UseBearerRouteLogging();

        return (sink, logger, app);
    }

    private static string FindBackendRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CodeSpace.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the CodeSpace backend root.");
    }

    private sealed class CapturedEvents : ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Events { get; } = new();

        public string Rendered => string.Join("\n", Events.Select(e => e.RenderMessage() + " " + string.Join(" ", e.Properties.Select(p => $"{p.Key}={p.Value}"))));

        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
    }
}
