using Autofac;
using Autofac.Extensions.DependencyInjection;
using CodeSpace.Core;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Settings.Application;
using CodeSpace.Core.Settings.Database;
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

        var application = new SerilogApplicationSetting(configuration).Value;

        Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", application)
            .WriteTo.Console()
            .CreateLogger();

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
