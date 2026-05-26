using System.Text.Json.Serialization;
using CodeSpace.Api.Extensions;
using CodeSpace.Api.Filters;
using CodeSpace.Core.Services.Auth;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.OAuth;
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
            endpoints.MapControllers();
            if (env.IsDevelopment()) endpoints.MapOpenApi();
        });
    }
}
