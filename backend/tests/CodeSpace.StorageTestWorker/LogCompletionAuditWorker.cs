using System.Text.Json;
using Autofac;
using Autofac.Extensions.DependencyInjection;
using CodeSpace.Core;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents.AgentRunLogging;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace CodeSpace.StorageTestWorker;

/// <summary>A killable production-container consumer. The parent owns the real PostgreSQL database and CAS files.</summary>
public static class LogCompletionAuditWorker
{
    public const string ConnectionEnvironmentVariable = "CODESPACE_LOG_AUDIT_CONNECTION";

    public static async Task<int> RunAsync(string requestJson)
    {
        var request = JsonSerializer.Deserialize<AgentRunLogCompleteRequest>(requestJson) ?? throw new ArgumentException("Missing completion request.");
        var connection = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable) ?? throw new InvalidOperationException("Missing audit PostgreSQL connection.");
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CodeSpaceStore:ConnectionString"] = connection,
            ["Authentication:Jwt:SymmetricKey"] = "test-only-key-do-not-use-in-prod-minimum-32-chars",
            ["OAuth:CallbackUrl"] = "http://localhost:5099/api/credentials/oauth/callback",
        }).Build();
        using var logger = new LoggerConfiguration().CreateLogger();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddDataProtection();
        services.AddHttpContextAccessor();
        services.AddHttpClient();
        var builder = new ContainerBuilder();
        builder.Populate(services);
        builder.RegisterModule(new CodeSpaceModule(logger, configuration));
        await using var container = builder.Build();
        await using var scope = container.BeginLifetimeScope();
        LogCompletionReadProbe? probe = null;
        probe = new LogCompletionReadProbe(scope.Resolve<IArtifactCasRuntimeCoordinator>(), async cancellationToken =>
        {
            Console.WriteLine($"verified-prefix:{JsonSerializer.Serialize(probe!.Reads.ToArray())}");
            await Console.Out.FlushAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        });
        var logs = new AgentRunLogService(scope.Resolve<DbContextOptions<CodeSpaceDbContext>>(), probe, TimeProvider.System);
        await logs.CompleteAsync(request, CancellationToken.None).ConfigureAwait(false);
        throw new InvalidOperationException("The audit child should be killed at its verified-prefix barrier.");
    }
}
