using CodeSpace.Core.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Core.Jobs;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Jobs;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace CodeSpace.Api.Extensions.Hangfire;

/// <summary>
/// The PROCESSING role: adds the Hangfire servers, mounts the dashboard, and scans every
/// <see cref="IRecurringJob"/> implementation into the recurring-job scheduler through
/// <see cref="IJobSafeRunner"/>. Selected by <c>HangfireHosting=Worker</c>, which is also the default,
/// so an all-in-one process is unchanged.
///
/// <para>Agent runs are Hangfire jobs, so <c>AgentRunExecutor.ExecuteAsync</c> — and with it the per-run
/// MCP endpoint, the sandbox and the UDS socket — runs ONLY where a server runs, i.e. only here. That is
/// what makes "public surface" and "agent/MCP surface" separately deployable: the Api role opens neither.</para>
/// </summary>
public class WorkerHangfireRegistrar : HangfireRegistrarBase
{
    public override void RegisterHangfire(IServiceCollection services, IConfiguration configuration)
    {
        // Storage + serializer + the job client come from the base, which the Api role registers too — a
        // public pod must still be able to push jobs onto the shared queue.
        base.RegisterHangfire(services, configuration);

        // TWO dedicated worker pools so a saturated agent pool can never starve the control plane. A long
        // agent.run run holds a worker for minutes (a codex/claude child runs); isolating the IAgentRunExecutor
        // jobs onto their OWN server — not just queue order, which only biases a FREE worker — guarantees the
        // control-plane jobs (wait/resume, recurring reconcilers/expiry, webhooks) keep their own capacity.
        //
        // KNOWN RESIDUAL (tracked separately): the agent.run node PARKS and dispatches its executor here, so its
        // engine walk is short — but a SYNCHRONOUS long node (agent.run_command, the git open-PR/review nodes, the
        // supervisor acceptance grader) runs inline ON the engine job, which is on the control pool. So this split
        // isolates the agent.run EXECUTOR, not every long job; a command-heavy engine walk can still occupy a
        // control worker for its timeout. Fully closing it needs those nodes dispatched off the control pool too.
        // Both pools run only on a processing (worker) pod.

        // Control-plane pool — short, low-volume jobs. A small fixed pool is plenty and is never blocked by agents.
        services.AddHangfireServer(opt =>
        {
            opt.WorkerCount = ControlWorkerCount;
            opt.ServerName = $"codespace-control-{Environment.MachineName}";
            opt.Queues = new[] { HangfireConstants.DefaultQueue };
        });

        // Agent pool — the long IAgentRunExecutor jobs. (WorkerCount preserves the prior agent concurrency; tune
        // it down for a child-process-heavy pod in a follow-up rather than changing throughput here.)
        services.AddHangfireServer(opt =>
        {
            opt.WorkerCount = Environment.ProcessorCount * 2;
            opt.ServerName = $"codespace-agents-{Environment.MachineName}";
            opt.Queues = new[] { HangfireConstants.AgentQueue };
        });
    }

    /// <summary>Dedicated control-plane workers — a deliberate small FLOOR (control jobs are short + low-volume + self-drain at the 2s poll interval, so they need little). Raise it if control-plane latency is ever observed under a recovery burst (many simultaneous resumes / reconciler re-dispatches / decision-expiry).</summary>
    private const int ControlWorkerCount = 4;

    public override void ApplyHangfire(IApplicationBuilder app, IConfiguration configuration)
    {
        base.ApplyHangfire(app, configuration);

        Log.Information("Hangfire role {Role}: this pod runs the job servers, owns recurring scheduling, and executes agent runs", HangfireHosting.Worker);
        // WORKER-ONLY: agent runs (and thus the per-run MCP endpoint) execute only on a processing pod, so the
        // deploy-time tool-fabric readiness diagnostic belongs here — a public (non-processing) pod never opens an
        // endpoint, so a missing proxy binary there is irrelevant. Surfaces a clear Warning when the endpoint is
        // enabled but the codespace-mcp proxy is missing (fail-closed → tool-less runs), so it's caught at boot, not
        // hours later. No-op + never-throws when the endpoint is off.
        AgentRunExecutor.LogMcpProxyReadiness(app.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger<AgentRunExecutor>());

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

            // A conditionally-registered job (feature-gated, e.g. a flag-OFF lane's reaper) is skipped ENTIRELY when it
            // opts out — no recurring entry is created, so flag-OFF is byte-identical (no tick ever fires).
            if (job is IConditionalRecurringJob { ShouldRegister: false })
            {
                Log.Information("Recurring job {Job} skipped — conditional registration is off", job.GetType().FullName);
                continue;
            }

            if (string.IsNullOrEmpty(job.CronExpression))
            {
                Log.Error("Recurring job cron expression empty, {Job}", job.GetType().FullName);
                continue;
            }

            backgroundJobClient.AddOrUpdateRecurringJob<IJobSafeRunner>(job.JobId, r => r.Run(job.JobId, type), job.CronExpression, job.TimeZone);
        }
    }
}
