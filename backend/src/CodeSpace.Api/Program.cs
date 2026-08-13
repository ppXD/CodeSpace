using Autofac;
using Autofac.Extensions.DependencyInjection;
using CodeSpace.Core;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Settings;
using CodeSpace.Core.Settings.Application;
using CodeSpace.Core.Settings.Database;
using CodeSpace.Core.Settings.Logging;
using Serilog;

namespace CodeSpace.Api;

public class Program
{
    public static void Main(string[] args)
    {
        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";

        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json")
            .AddJsonFile($"appsettings.{environment}.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        // Before anything reads a value: a placeholder the release tooling never substituted is set, non-empty,
        // and therefore passes every "is it configured" check below. It has to be caught here or it is caught
        // later as a permission error on a path named after the variable itself.
        CodeSpace.Core.Settings.ConfigurationPlaceholderGuard.ThrowIfUnrendered(configuration);

        // The deployment-varying settings that deep static call sites read (sandbox confinement, spool + artifact
        // roots, the drain budget). Bound here because Main runs BEFORE the host, so CodeSpaceModule's own binding
        // would be too late for anything on this path.
        RuntimeSettings.Bind(configuration);

        // P2 slice 3 (the production /tmp ban): a Production host with unconfigured durable roots refuses to
        // start — artifact blobs and re-attach spools must never silently land under the system temp dir.
        CodeSpace.Core.Settings.DurableRootsGuard.ThrowIfProductionUnconfigured(RuntimeSettings.Current, environment);

        var application = new SerilogApplicationSetting(configuration).Value;

        Log.Logger = BuildLogger(configuration, application);

        try
        {
            Log.Information("Configuring {Application} host...", application);

            new DbUpRunner(new CodeSpaceConnectionString(configuration).Value).Run();

            var host = CreateHostBuilder(args).Build();

            Log.Information("Starting {Application} host...", application);

            host.Run();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "{Application} terminated unexpectedly", application);
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    /// <summary>
    /// Console for whoever is watching the process right now, Seq for anything that has to be searched afterwards.
    /// Both roles of the topology come through here — <c>Dockerfile.api</c> and <c>Dockerfile.worker</c> run this
    /// same assembly and differ only by <c>HangfireHosting</c> — so the worker's agent-run logs reach Seq too;
    /// there is no second entry point building its own logger.
    ///
    /// <para>An unreachable Seq changes nothing the operator can see beyond the console. <c>WriteTo.Seq</c> is the
    /// BATCHED sink: it opens no connection while the logger is being built, and posts from a background timer, so
    /// a failed post is retried and dropped there rather than surfacing at the call site. (Serilog's contract for
    /// the other overload, <c>AuditTo.Seq</c>, is the opposite and says so — "failures will propagate to the caller
    /// immediately as exceptions". We are deliberately not using it.) The in-memory queue is bounded, so a Seq that
    /// stays down costs a capped buffer, not the process.</para>
    ///
    /// <para><c>Log.CloseAndFlush</c> in <see cref="Main"/> drains the last batch on the way out, and that is the
    /// one place an unreachable Seq CAN be felt: a server that refuses the connection fails fast, but one that
    /// accepts it and never answers holds the flush for the HTTP client's own timeout — measured at roughly two
    /// hundred seconds, which is a shutdown that looks like a hang. Hence the explicit
    /// <see cref="Logging.BoundedSeqPostHandler"/>.</para>
    /// </summary>
    private static Serilog.ILogger BuildLogger(IConfiguration configuration, string application)
    {
        var logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", application)
            .WriteTo.Console();

        var seqServerUrl = new SerilogServerUrlSetting(configuration).Value;

        // Blank ServerUrl is how a deployment says "console only" — see SerilogServerUrlSetting.
        if (string.IsNullOrWhiteSpace(seqServerUrl)) return logger.CreateLogger();

        var seqApiKey = new SerilogApiKeySetting(configuration).Value;

        try
        {
            // Blank key means anonymous ingestion, which is what a local Seq accepts; pass null rather than an
            // empty header value so the sink's own "no key configured" path is the one that runs.
            //
            // The handler is the whole reason this is not a one-liner. Its default leaves a batch waiting on a
            // server that accepted the socket and then went quiet, and CloseAndFlush inherits that wait at
            // shutdown — see BoundedSeqPostHandler.
            return logger
                .WriteTo.Seq(seqServerUrl, apiKey: string.IsNullOrWhiteSpace(seqApiKey) ? null : seqApiKey, messageHandler: new CodeSpace.Api.Logging.BoundedSeqPostHandler())
                .CreateLogger();
        }
        catch (Exception ex)
        {
            // Logging must never be the reason the product will not start. An unreachable Seq already costs
            // nothing — the sink is batched — but a MALFORMED one is a different failure: a ServerUrl that is not
            // a URL at all throws while the sink is being constructed, before any of that batching exists.
            //
            // Swallowing it would be its own trap, so the console — which is already attached and is the thing an
            // operator is watching during a boot — says what happened and that logs are console-only until it is
            // fixed. The process comes up serving requests either way.
            var consoleOnly = logger.CreateLogger();

            consoleOnly.Error(ex, "Seq is configured as {SeqServerUrl} but the sink could not be built; continuing with console logging only. Fix {ConfigurationKey}, or blank it to turn Seq off deliberately", seqServerUrl, SerilogServerUrlSetting.ConfigurationKey);

            return consoleOnly;
        }
    }

    /// <summary>
    /// Conventional <c>(string[]) =&gt; IHostBuilder</c> factory. The standard signature matters:
    /// <c>WebApplicationFactory</c>'s <c>HostFactoryResolver</c> discovers this method and builds
    /// the host in-memory for E2E tests WITHOUT running <see cref="Main"/> — which would otherwise
    /// re-run the startup <c>DbUpRunner</c> against the configured (production) database. Config +
    /// logger come from the host-builder context so the same wiring serves both <c>dotnet run</c>
    /// and the test host.
    /// </summary>
    public static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .UseSerilog()
            .UseServiceProviderFactory(new AutofacServiceProviderFactory())
            .ConfigureContainer<ContainerBuilder>((context, builder) =>
            {
                builder.RegisterModule(new CodeSpaceModule(Log.Logger, context.Configuration));
            })
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseStartup<Startup>();
            });
}
