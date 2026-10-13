using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CodeSpace.Api.Logging;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Users;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Shouldly;

namespace CodeSpace.E2ETests.Auth;

/// <summary>
/// Invitation, password-reset and workflow-callback tokens are bearer credentials that travel in URL PATHS. Every request
/// used to write its raw path to the console and Seq — in Serilog's completion line and as <c>RequestPath</c> on every
/// event logged while it ran — so anyone who could read the logs could join a team, take over an account or resume a run.
/// This drives each of those routes through the real pipeline, on success and on failure, with the process logger built
/// the way <c>Program.BuildLogger</c> builds it (the shipped <c>Serilog</c> settings and <c>Enrich.FromLogContext</c>), and
/// reads back every event the sink received. It also sends each token where no bearer route answers — a GET or HEAD on
/// the callback URL, a wrong method, the SPA's links on the API origin — and as the first request of a fresh
/// non-Development pod, whose https-port warning is logged before routing.
///
/// <para>Tier: High fidelity. The real app host and its middleware order, real Postgres, the real engine drained for the
/// callback run, the real invitation, reset and callback services. The sink is the only addition: it replaces console
/// and Seq, which receive exactly these events. The suite runs serially (AssemblyParallelization), so swapping the
/// process-wide <c>Log.Logger</c> for the length of the test cannot capture another test's events.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class BearerUrlTokenLogE2ETests
{
    [Fact]
    public async Task Invitation_reset_and_callback_tokens_in_url_paths_reach_no_log_event_on_success_or_failure()
    {
        var sink = new CapturedEvents();
        var previous = Log.Logger;
        Log.Logger = ProductionShapedLogger(sink);
        var factory = new TaskLaunchApiFactory();
        await ((IAsyncLifetime)factory).InitializeAsync();

        try
        {
            var world = await SeedWorldAsync(factory);
            var anonymous = factory.CreateClient();

            var invitation = TokenAfterLastSlash(await ReadStringAsync(await SendAsync(factory, HttpMethod.Post, "/api/teams/invitations", world, new { email = $"invitee-{world.Suffix}@test.local", role = "Member" }), "inviteUrl"));
            (await anonymous.GetAsync($"/api/invitations/{invitation}")).StatusCode.ShouldBe(HttpStatusCode.OK, "preview");
            (await anonymous.PostAsJsonAsync($"/api/invitations/{invitation}/accept", new { name = "Too Short", password = "x" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest, "a refused accept leaves the token valid — the case where a logged token is still usable");
            (await anonymous.PostAsJsonAsync($"/api/invitations/{invitation}/accept", new { name = $"Joined {world.Suffix}", password = "correct-horse-battery" })).StatusCode.ShouldBe(HttpStatusCode.OK, "accept");

            var reset = TokenAfterLastSlash((await IssueResetLinkAsync(factory, world.MemberId)).ResetUrl);
            (await anonymous.PostAsJsonAsync($"/api/auth/reset-password/{reset}", new { newPassword = "short" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest, "a refused reset leaves the token valid");
            (await anonymous.PostAsJsonAsync($"/api/auth/reset-password/{reset}", new { newPassword = "recovered-passphrase-x" })).StatusCode.ShouldBe(HttpStatusCode.NoContent, "reset");

            var callbackPath = await ParkCallbackAndRevealAsync(factory, world);
            var callback = TokenAfterLastSlash(callbackPath);
            var guessed = Guid.NewGuid().ToString("N");
            (await anonymous.PostAsync($"/api/workflows/callbacks/{guessed}", Json("{}"))).StatusCode.ShouldBe(HttpStatusCode.NotFound, "a guessed callback token");
            (await anonymous.PostAsync(callbackPath, Json("""{"status":"done"}"""))).StatusCode.ShouldBe(HttpStatusCode.OK, "the callback");

            // The capture is live and saw these requests — so the absence below is a measurement, not an empty sink.
            sink.CompletionPaths().ShouldContain("/api/invitations/{token}", customMessage: "the preview's completion line logs the route template. If the sink holds no completion lines at all, Log.Logger was not the host's logger — check TaskLaunchApiFactory still builds through Program.CreateHostBuilder's UseSerilog()");
            sink.CompletionPaths().ShouldContain("/api/invitations/{token}/accept");
            sink.CompletionPaths().ShouldContain("/api/auth/reset-password/{token}");
            sink.CompletionPaths().ShouldContain("/api/workflows/callbacks/{token:length(32)}");
            sink.Events.ShouldContain(e => e.Properties.ContainsKey("RequestName") && e.Properties["RequestPath"].ToString() == "\"/api/invitations/{token}/accept\"", "a mediator line inside the accept carries the template as its hosting-scope RequestPath");

            foreach (var (name, token) in new[] { ("invitation", invitation), ("reset", reset), ("callback", callback), ("guessed callback", guessed) })
                sink.Rendered.ShouldNotContain(token, customMessage: $"the {name} token from a URL path reached a log event:\n{sink.LinesContaining(token)}");
        }
        finally
        {
            Log.Logger = previous;
            await ((IAsyncLifetime)factory).DisposeAsync();
        }
    }

    [Fact]
    public async Task Valid_tokens_sent_with_a_method_or_on_a_path_no_bearer_route_answers_reach_no_log_event()
    {
        // The fallback answers these, not a bearer route: a link unfurler or a person pasting the callback URL into a browser
        // sends GET, an uptime check HEAD; a wrong method on an invitation or reset route; the SPA's invite or reset link
        // built against the API origin. Each token is still valid afterwards — what a log reader would hold is live.
        var sink = new CapturedEvents();
        var previous = Log.Logger;
        Log.Logger = ProductionShapedLogger(sink);
        var factory = new TaskLaunchApiFactory();
        await ((IAsyncLifetime)factory).InitializeAsync();

        try
        {
            var world = await SeedWorldAsync(factory);
            var anonymous = factory.CreateClient();

            var invitation = TokenAfterLastSlash(await ReadStringAsync(await SendAsync(factory, HttpMethod.Post, "/api/teams/invitations", world, new { email = $"invitee-{world.Suffix}@test.local", role = "Member" }), "inviteUrl"));
            var reset = TokenAfterLastSlash((await IssueResetLinkAsync(factory, world.MemberId)).ResetUrl);
            var callbackPath = await ParkCallbackAndRevealAsync(factory, world);
            var callback = TokenAfterLastSlash(callbackPath);

            await anonymous.GetAsync(callbackPath);
            await anonymous.SendAsync(new HttpRequestMessage(HttpMethod.Head, callbackPath));
            await anonymous.GetAsync($"/api/invitations/{invitation}/accept");
            await anonymous.GetAsync($"/api/auth/reset-password/{reset}");
            await anonymous.GetAsync($"/invite/{invitation}");
            await anonymous.GetAsync($"/reset-password/{reset}");

            (await anonymous.GetAsync($"/api/invitations/{invitation}")).StatusCode.ShouldBe(HttpStatusCode.OK, "the invitation token stayed valid");
            (await anonymous.PostAsync(callbackPath, Json("""{"status":"done"}"""))).StatusCode.ShouldBe(HttpStatusCode.OK, "the callback token stayed valid");

            var paths = sink.CompletionPaths();
            paths.Count(p => p == "/api/workflows/callbacks/{token}").ShouldBe(2, $"the GET and the HEAD on the callback URL each logged a completion line, masked. Logged: {string.Join(", ", paths)}");
            paths.ShouldContain("/api/invitations/{token}/accept");
            paths.ShouldContain("/api/auth/reset-password/{token}");
            paths.ShouldContain("/invite/{token}");
            paths.ShouldContain("/reset-password/{token}");

            foreach (var (name, token) in new[] { ("invitation", invitation), ("reset", reset), ("callback", callback) })
                sink.Rendered.ShouldNotContain(token, customMessage: $"the {name} token from a URL path reached a log event:\n{sink.LinesContaining(token)}");
        }
        finally
        {
            Log.Logger = previous;
            await ((IAsyncLifetime)factory).DisposeAsync();
        }
    }

    [Fact]
    public async Task The_first_request_of_a_fresh_non_development_api_logs_no_token_before_routing()
    {
        // UseHttpsRedirection runs ahead of routing and, in a non-Development host with no https port (the shipped compose
        // API: Production, ASPNETCORE_URLS=http://+:8080), warns once per process — on whichever request comes first. A
        // sibling host on the same database stands in for a pod that just started; its first request is a callback.
        var sink = new CapturedEvents();
        var previous = Log.Logger;
        Log.Logger = ProductionShapedLogger(sink);
        var factory = new TaskLaunchApiFactory();
        await ((IAsyncLifetime)factory).InitializeAsync();
        NonDevelopmentApiFactory? freshPod = null;

        try
        {
            var world = await SeedWorldAsync(factory);
            var callbackPath = await ParkCallbackAndRevealAsync(factory, world);
            var callback = TokenAfterLastSlash(callbackPath);

            freshPod = new NonDevelopmentApiFactory(factory.ConnectionString);
            var client = freshPod.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            (await client.PostAsync(callbackPath, Json("""{"status":"done"}"""))).StatusCode.ShouldBe(HttpStatusCode.OK, "the fresh pod's first request is the callback, and it resumes the run");

            sink.Events.ShouldContain(e => SourceOf(e) == "Microsoft.AspNetCore.HttpsPolicy.HttpsRedirectionMiddleware" && e.Properties["RequestPath"].ToString() == "\"/api/workflows/callbacks/{token}\"", $"the https-port warning fired on the callback and carried the masked path. If no such event exists at all, the host found an https port and this test measures nothing — check NonDevelopmentApiFactory still runs outside Development. Events:\n{sink.Rendered}");
            sink.Rendered.ShouldNotContain(callback, customMessage: $"the callback token reached a log event:\n{sink.LinesContaining(callback)}");
        }
        finally
        {
            Log.Logger = previous;
            if (freshPod != null) await freshPod.DisposeAsync();
            await ((IAsyncLifetime)factory).DisposeAsync();
        }
    }

    [Fact]
    public async Task Every_route_that_binds_a_credential_looking_parameter_is_redacted_from_the_log()
    {
        // The redaction is keyed on the route parameter's NAME. A new route that names a credential anything else would be
        // logged raw, so every real endpoint whose parameter reads like one must be one the redaction recognises.
        var factory = new TaskLaunchApiFactory();
        await ((IAsyncLifetime)factory).InitializeAsync();

        try
        {
            var routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
            var credentialShaped = routes.Where(r => r.RoutePattern.Parameters.Any(p => p.Name.Contains("token", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("secret", StringComparison.OrdinalIgnoreCase))).ToList();
            var templates = credentialShaped.Select(r => r.RoutePattern.RawText).ToList();

            templates.ShouldContain("api/invitations/{token}");
            templates.ShouldContain("api/invitations/{token}/accept");
            templates.ShouldContain("api/auth/reset-password/{token}");
            templates.ShouldContain("api/workflows/callbacks/{token:length(32)}");

            foreach (var route in credentialShaped)
                route.RoutePattern.Parameters.ShouldContain(p => BearerRouteLogging.BearerParameters.Contains(p.Name), $"{route.RoutePattern.RawText} binds a credential-looking parameter the log redaction does not key on; name it 'token' or add its name to BearerRouteLogging.BearerParameters");
        }
        finally
        {
            await ((IAsyncLifetime)factory).DisposeAsync();
        }
    }

    // ─── World ──────────────────────────────────────────────────────────────────

    private sealed record World(Guid TeamId, Guid OwnerId, Guid MemberId, string Suffix);

    /// <summary>The process logger as <c>Program.BuildLogger</c> makes it: shipped levels, <c>Enrich.FromLogContext</c>, and a sink in place of console + Seq.</summary>
    private static Serilog.ILogger ProductionShapedLogger(ILogEventSink sink)
    {
        var shipped = new ConfigurationBuilder().AddJsonFile(Path.Combine(FindBackendRoot(), "src", "CodeSpace.Api", "appsettings.json")).Build();

        return new LoggerConfiguration().ReadFrom.Configuration(shipped).Enrich.FromLogContext().WriteTo.Sink(sink).CreateLogger();
    }

    private static async Task<World> SeedWorldAsync(TaskLaunchApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var teamId = Guid.NewGuid();
        var suffix = teamId.ToString("N")[..8];
        db.Team.Add(new Team { Id = teamId, Slug = $"logs-{suffix}", Name = "Logs", Kind = TeamKind.Workspace, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });

        Guid Member(TeamRole role)
        {
            var userId = Guid.NewGuid();
            db.User.Add(new User { Id = userId, SecurityStamp = TestToken.SeedStamp, Email = $"logs-{role}-{suffix}@test.local", Name = $"logs {role} {suffix}", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
            db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = role, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
            return userId;
        }

        var world = new World(teamId, Member(TeamRole.Owner), Member(TeamRole.Member), suffix);
        await db.SaveChangesAsync();

        return world;
    }

    /// <summary>Issued through the service, as SessionRevocationE2ETests does: the admin endpoint needs a deployment admin, and the token's route is what is under test.</summary>
    private static async Task<CodeSpace.Messages.Dtos.Users.PasswordResetLink> IssueResetLinkAsync(TaskLaunchApiFactory factory, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IAccountLifecycleService>().IssueResetAsync(userId, CancellationToken.None);
    }

    /// <summary>Owner authors and runs trigger → flow.wait_callback → terminal; the engine parks it; the Owner reveals the URL.</summary>
    private static async Task<string> ParkCallbackAndRevealAsync(TaskLaunchApiFactory factory, World world)
    {
        var workflow = new
        {
            name = $"logs-{Guid.NewGuid():N}"[..20],
            definition = new
            {
                schemaVersion = 1,
                nodes = new[]
                {
                    new { id = "start", typeKey = "trigger.manual", config = new { }, inputs = new { } },
                    new { id = "cb", typeKey = "flow.wait_callback", config = new { }, inputs = new { } },
                    new { id = "end", typeKey = "builtin.terminal", config = new { }, inputs = new { } },
                },
                edges = new[] { new { from = "start", to = "cb" }, new { from = "cb", to = "end" } },
            },
            activations = Array.Empty<object>(),
            enabled = true,
        };

        var workflowId = Guid.Parse(await ReadStringAsync(await SendAsync(factory, HttpMethod.Post, "/api/workflows", world, workflow), "id"));
        var runId = Guid.Parse(await ReadStringAsync(await SendAsync(factory, HttpMethod.Post, $"/api/workflows/{workflowId}/run", world, new { }), "runId"));
        await factory.JobClient.DrainAsync();

        var pending = await SendAsync(factory, HttpMethod.Get, $"/api/workflows/runs/{runId}/pending-wait", world);
        var waitId = JsonDocument.Parse(await pending.Content.ReadAsStringAsync()).RootElement.GetProperty("wait").GetProperty("id").GetGuid();

        return await ReadStringAsync(await SendAsync(factory, HttpMethod.Post, $"/api/workflows/runs/{runId}/waits/{waitId}/callback-url", world), "path");
    }

    private static async Task<HttpResponseMessage> SendAsync(TaskLaunchApiFactory factory, HttpMethod method, string url, World world, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestToken.Mint(world.OwnerId, TestToken.SeedStamp));
        request.Headers.Add("X-Team-Id", world.TeamId.ToString());
        if (body != null) request.Content = JsonContent.Create(body);

        return await factory.CreateClient().SendAsync(request);
    }

    private static async Task<string> ReadStringAsync(HttpResponseMessage response, string property)
    {
        var text = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.ShouldBeTrue($"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.AbsolutePath} answered {(int)response.StatusCode}: {text}");

        var value = JsonDocument.Parse(text).RootElement.GetProperty(property);
        return value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
    }

    private static string TokenAfterLastSlash(string url) => url[(url.LastIndexOf('/') + 1)..];

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static string? SourceOf(LogEvent logEvent) => logEvent.Properties.TryGetValue("SourceContext", out var source) ? ((ScalarValue)source).Value?.ToString() : null;

    /// <summary>The app as a deployed API pod runs it — Production, so UseHttpsRedirection is on — over another fixture's database; never initialised as a fixture of its own.</summary>
    private sealed class NonDevelopmentApiFactory : TaskLaunchApiFactory
    {
        private readonly string _connectionString;

        public NonDevelopmentApiFactory(string connectionString) { _connectionString = connectionString; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CodeSpaceStore:ConnectionString"] = _connectionString,
                [CodeSpace.Core.Settings.Application.PublicBaseUrlSetting.ConfigurationKey] = "http://spa.test",
                [CodeSpace.Core.Settings.Webhooks.WebhookBaseUrlSetting.ConfigurationKey] = "http://api.test",
            }));
        }
    }

    private static string FindBackendRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CodeSpace.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the CodeSpace backend root.");
    }

    /// <summary>Every event the host's logger let through, rendered with every property — what a console template or a Seq query could show.</summary>
    private sealed class CapturedEvents : ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Events { get; } = new();

        public string Rendered => string.Join("\n", Lines());

        public IReadOnlyList<string> CompletionPaths() =>
            Events.Where(e => e.Properties.ContainsKey("StatusCode") && e.Properties.ContainsKey("Elapsed") && e.Properties.ContainsKey("RequestPath"))
                .Select(e => ((ScalarValue)e.Properties["RequestPath"]).Value?.ToString() ?? "")
                .ToList();

        public string LinesContaining(string value) => string.Join("\n", Lines().Where(l => l.Contains(value, StringComparison.Ordinal)));

        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);

        private IEnumerable<string> Lines() =>
            Events.Select(e => $"[{e.Level}] {e.RenderMessage()} {e.Exception} :: {string.Join(" | ", e.Properties.Select(p => $"{p.Key}={p.Value}"))}");
    }
}
