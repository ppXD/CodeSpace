using CodeSpace.Core.Constants;
using CodeSpace.Core.Jobs;
using CodeSpace.Core.Services.Jobs;
using Hangfire;
using Serilog;

namespace CodeSpace.Api.Extensions.Hangfire;

/// <summary>
/// Concrete registrar that adds the Hangfire server, mounts the dashboard, and scans every
/// <see cref="IRecurringJob"/> implementation into the recurring-job scheduler through
/// <see cref="IJobSafeRunner"/>.
/// </summary>
public class CodeSpaceHangfireRegistrar : HangfireRegistrarBase
{
    /// <summary>
    /// Deployment-topology gate. DEFAULT-ON, opt-OUT: processing stays ON unless this is explicitly
    /// set to "0"/"false" (trimmed, case-insensitive). Set it to "0"/"false" on a public-facing API pod
    /// so that pod serves HTTP/webhooks and ENQUEUES jobs to the shared Postgres queue WITHOUT processing
    /// them — dedicated worker pods (env unset) process the queue. This makes the "public API (no worker)
    /// + N worker pods" topology a supported mode; env unset keeps today's all-in-one pod byte-identical.
    ///
    /// <para>NOTE the polarity is the OPPOSITE of the fail-closed default-OFF feature flags
    /// (e.g. <c>CODESPACE_AGENT_MCP_ENDPOINT_ENABLED</c>): here the SAFE default is to KEEP processing,
    /// because processing-everywhere is today's behaviour. So this gate is default-ON, opt-out.</para>
    ///
    /// <para>This is also an MCP-safety property. Agent runs are Hangfire jobs
    /// (<c>_backgroundJobClient.Enqueue&lt;IAgentRunExecutor&gt;(e =&gt; e.ExecuteAsync(...))</c>; the reconciler
    /// enqueues ReattachAsync), so <c>AgentRunExecutor.ExecuteAsync</c> runs ONLY where a Hangfire server runs.
    /// The per-run MCP endpoint + sandbox + UDS socket are opened in-process on THAT pod (pod-local). A
    /// processing-OFF (public) pod never runs ExecuteAsync → never opens an MCP endpoint, so MCP cannot be
    /// affected there — the public surface and the agent/MCP surface are deployable on separate pods.</para>
    ///
    /// Pinned by a test (Rule 8) — renaming it silently turns processing back on for an operator who
    /// deployed a public pod expecting it OFF.
    /// </summary>
    public const string ProcessingEnabledEnvVar = "CODESPACE_HANGFIRE_PROCESSING_ENABLED";

    /// <summary>Default-ON, opt-OUT: TRUE unless the env var (trimmed, case-insensitive) is exactly "0" or "false". Mirrors the trim/case handling of <c>AgentRunExecutor.IsMcpEndpointEnabled</c> but INVERTED (safe default keeps processing). Internal so it's unit-pinned; production reads it through this single gate.</summary>
    internal static bool IsProcessingEnabled() => IsProcessingEnabled(Environment.GetEnvironmentVariable(ProcessingEnabledEnvVar));

    /// <summary>Pure overload (no env read) so the polarity is unit-testable without env mutation: FALSE only for "0"/"false" (trimmed, case-insensitive); TRUE for null / "" / anything else.</summary>
    internal static bool IsProcessingEnabled(string? raw)
    {
        var value = raw?.Trim();

        return value is not ("0" or "false" or "False" or "FALSE");
    }

    public override void RegisterHangfire(IServiceCollection services, IConfiguration configuration)
    {
        // Storage + serializer + the job client are ALWAYS registered so enqueue works on every pod —
        // a public pod must still be able to push jobs onto the shared queue.
        base.RegisterHangfire(services, configuration);

        if (!IsProcessingEnabled()) return;

        services.AddHangfireServer(opt =>
        {
            opt.WorkerCount = Environment.ProcessorCount * 2;
            opt.ServerName = $"codespace-{Environment.MachineName}";
            opt.Queues = new[] { HangfireConstants.DefaultQueue };
        });
    }

    public override void ApplyHangfire(IApplicationBuilder app, IConfiguration configuration)
    {
        base.ApplyHangfire(app, configuration);

        // The dashboard stays available on every pod (a public/admin pod may still SHOW jobs).
        app.UseHangfireDashboard("/hangfire", new DashboardOptions
        {
            Authorization = new[] { new HangfireDashboardAuthFilter() },
            DashboardTitle = "CodeSpace jobs",
        });

        if (!IsProcessingEnabled())
        {
            Log.Information("Hangfire processing disabled ({EnvVar}={Value}): this pod enqueues but does not process jobs", ProcessingEnabledEnvVar, Environment.GetEnvironmentVariable(ProcessingEnabledEnvVar));
            return;
        }

        // A non-processing pod must NOT own recurring-job scheduling/execution.
        ScanHangfireRecurringJobs(app);
    }

    /// <summary>
    /// Resolve every <see cref="IRecurringJob"/> from DI and register each with Hangfire's
    /// scheduler. The registered call is <c>IJobSafeRunner.Run(jobId, jobType)</c> — Hangfire
    /// serialises that call; on each tick a fresh lifetime scope resolves the concrete job
    /// type from DI + invokes <see cref="IJob.Execute"/> with <c>JobId</c> in the log context.
    /// </summary>
    private static void ScanHangfireRecurringJobs(IApplicationBuilder app)
    {
        var backgroundJobClient = app.ApplicationServices.GetRequiredService<ICodeSpaceBackgroundJobClient>();

        var recurringJobTypes = typeof(IRecurringJob).Assembly.GetTypes()
            .Where(type => type.IsClass && !type.IsAbstract && typeof(IRecurringJob).IsAssignableFrom(type))
            .ToList();

        foreach (var type in recurringJobTypes)
        {
            var job = (IRecurringJob)app.ApplicationServices.GetRequiredService(type);

            if (string.IsNullOrEmpty(job.CronExpression))
            {
                Log.Error("Recurring job cron expression empty, {Job}", job.GetType().FullName);
                continue;
            }

            backgroundJobClient.AddOrUpdateRecurringJob<IJobSafeRunner>(job.JobId, r => r.Run(job.JobId, type), job.CronExpression, job.TimeZone);
        }
    }
}
