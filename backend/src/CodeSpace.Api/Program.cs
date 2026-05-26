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

            var host = CreateHostBuilder(args, configuration).Build();

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

    private static IHostBuilder CreateHostBuilder(string[] args, IConfiguration configuration) =>
        Host.CreateDefaultBuilder(args)
            .UseSerilog()
            .UseServiceProviderFactory(new AutofacServiceProviderFactory())
            .ConfigureContainer<ContainerBuilder>(builder =>
            {
                builder.RegisterModule(new CodeSpaceModule(Log.Logger, configuration));
            })
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseStartup<Startup>();
            });
}
