using System.Text.Json.Serialization;
using CodeSpace.Api.Extensions;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Api.Filters;
using CodeSpace.Core.Services.Auth;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.OAuth;
using CodeSpace.Core.Settings;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Serilog;

namespace CodeSpace.Api;

public class Startup
{
    private IConfiguration Configuration { get; }
    private IWebHostEnvironment Environment { get; }

    public Startup(IConfiguration configuration, IWebHostEnvironment environment)
    {
        Configuration = configuration;
        Environment = environment;
    }

    private const string CorsPolicyName = "CodeSpaceSpa";

    public void ConfigureServices(IServiceCollection services)
    {
        services
            .AddControllers(opts => opts.Filters.Add<GlobalExceptionFilter>())
            .AddJsonOptions(opts =>
            {
                // Serialize / deserialize every enum as its string name (e.g. "GitLab" /
                // "Owner" / "Active"). Without this, the default behaviour is integer
                // ordinals — which breaks JSON round-trip with the SPA (TypeScript unions
                // expect strings) and turns "provider": "GitLab" into a 400 validation
                // error on the way in.
                opts.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            });

        // Persist the Data Protection key-ring in the shared Postgres under a stable application name, so every
        // API/worker pod shares ONE key-ring and decrypts the same credentials (without this, the default per-pod
        // ephemeral key-ring breaks credential decryption across replicas / restarts). See CodeSpaceDataProtection.
        services.AddCodeSpaceDataProtection();

        // k8s probe surface. /health/live (liveness) carries NO checks — it's 200 whenever the process is up and
        // the pipeline responds, so a transient DB blip doesn't trigger a pod RESTART. /health/ready (readiness)
        // runs the "ready"-tagged checks — here a DbContext ping — so a pod is pulled from the Service's endpoints
        // (stops receiving traffic) while its DB is unreachable, without being killed. Mapped anonymous below.
        services.AddHealthChecks().AddDbContextCheck<CodeSpaceDbContext>("db", tags: new[] { "ready" });

        // CORS — needed when the SPA calls the backend directly (VITE_API_URL=http://localhost:5099)
        // instead of going through Vite's /api proxy. The Vite-proxy path is same-origin so CORS
        // is a no-op there; this only matters when an operator wires the SPA at the API host.
        //
        // Allow-list comes from configuration (`Cors:AllowedOrigins`, array of strings). In
        // Development we also accept the Vite dev port out of the box so the common
        // "fresh clone → npm run dev → dotnet run" workflow doesn't need extra setup.
        // The port (5180) is pinned by vite.config.ts server.port — keep these in sync.
        services.AddCors(options => options.AddPolicy(CorsPolicyName, builder =>
        {
            var configured = Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
            var origins = configured.ToList();

            if (Environment.IsDevelopment())
            {
                if (!origins.Contains("http://localhost:5180")) origins.Add("http://localhost:5180");
                if (!origins.Contains("http://127.0.0.1:5180")) origins.Add("http://127.0.0.1:5180");
            }

            builder
                .WithOrigins(origins.ToArray())
                .AllowAnyHeader()    // SPA sends Authorization + X-Team-Id + Content-Type
                .AllowAnyMethod()    // GET/POST/PATCH/DELETE/OPTIONS preflight
                .AllowCredentials(); // no cookies today, but keeps the door open for refresh-cookie flows
        }));

        services.AddOpenApi();
        services.AddHttpContextAccessor();
        // OAuth clients (GitHub / GitLab) and any future outbound HTTP consumer go through
        // IHttpClientFactory so we get proper socket lifecycle management + connection pool reuse.
        services.AddHttpClient();
        // Model reflection GETs an operator-supplied gateway with the decrypted key as a Bearer header, so it uses a
        // dedicated client with redirects DISABLED (a same-host https→http downgrade redirect would not strip the
        // header) and a tight timeout (an arbitrary gateway must never wedge the request thread on the 100s default).
        services.AddHttpClient(CodeSpace.Core.Services.Agents.ModelCredentials.Reflectors.LiteLLMOpenAIReflector.HttpClientName,
                c => c.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.SocketsHttpHandler { AllowAutoRedirect = false });
        // The in-process LLM clients (Anthropic / OpenAI-wire) resolve their HttpClient by name. Without a dedicated
        // registration they fell to the bare default above — a 100s wall that GUILLOTINES a slow reasoning / long
        // generation, no connection-lifetime tuning, no transient resilience. Register the hardened, resilient named
        // clients (generous tunable budget + SocketsHttpHandler + retry/Retry-After) — shared with the resilience test.
        services.AddLlmHttpClients();
        // Background janitor that sweeps expired oauth_pending_state rows every 5 minutes.
        // ConsumeAsync drops a row on successful flow, but abandoned flows (user closed tab)
        // would accumulate without this.
        services.AddHostedService<OAuthStateCleanupHostedService>();
        // Loud warning whenever a user still has the bootstrap password flag set — operators
        // running with the committed default credentials see this every 30 minutes.
        services.AddHostedService<UnrotatedBootstrapPasswordWarningHostedService>();
        // Hangfire wiring. Storage (Postgres, own schema) + worker pool. The dashboard +
        // recurring-job registration happen in Configure() below via UseCodeSpaceHangfire
        // because RecurringJob.AddOrUpdate needs a live IServiceProvider.
        services.AddCodeSpaceHangfire(Configuration);

        // Graceful-shutdown drain budget. On SIGTERM (rolling update / scale-down) the host stops
        // fetching new background work and lets in-flight Hangfire jobs finish within this window
        // before exiting — so a deploy doesn't sever short jobs mid-flight (they'd otherwise be
        // re-claimed ~5 min later via the invisibility timeout). The deployment's grace period MUST
        // be >= this (k8s: terminationGracePeriodSeconds); long agent runs that exceed it are
        // recovered by the reconciler, not drained. Tunable via CODESPACE_SHUTDOWN_DRAIN_SECONDS.
        services.Configure<HostOptions>(o => o.ShutdownTimeout = ShutdownSettings.ResolveDrainTimeout());

        services.AddCustomAuthentication(Configuration, Environment);
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        app.UseSerilogRequestLogging(opts =>
        {
            opts.EnrichDiagnosticContext = (diag, ctx) =>
            {
                var teamId = ctx.Request.Headers[HeaderCurrentTeam.HeaderName].ToString();
                if (!string.IsNullOrEmpty(teamId)) diag.Set("TeamId", teamId);
            };
        });
        if (!env.IsDevelopment()) app.UseHttpsRedirection();
        app.UseRouting();
        // CORS must run BEFORE auth — browsers send the preflight OPTIONS unauthenticated,
        // and without this the CORS middleware can't write the Access-Control-Allow-* headers
        // before auth rejects the request.
        app.UseCors(CorsPolicyName);
        app.UseAuthentication();
        app.UseAuthorization();

        // Mount /hangfire dashboard (admin-only via HangfireDashboardAuthFilter) + register
        // every IRecurringJob with Hangfire's scheduler. MUST run after UseAuthentication
        // so the filter can read HttpContext.User.
        app.UseCodeSpaceHangfire(Configuration);

        app.UseEndpoints(endpoints =>
        {
            // k8s liveness/readiness probes — AllowAnonymous because the global FallbackPolicy requires auth and a
            // probe is unauthenticated. Live = process up (no checks); Ready = the "ready"-tagged checks (DB ping).
            endpoints.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
            endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") }).AllowAnonymous();

            endpoints.MapControllers();
            if (env.IsDevelopment()) endpoints.MapOpenApi();
        });
    }
}
