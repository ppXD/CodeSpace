using System.ComponentModel;
using Autofac;
using CodeSpace.Core.DependencyInjection;
using Serilog.Context;

namespace CodeSpace.Core.Jobs;

/// <summary>
/// The indirection between Hangfire and the actual <see cref="IJob"/>. Hangfire serialises
/// a call to <see cref="Run"/>; at fire time the runner opens a fresh lifetime scope,
/// resolves the concrete job type from DI, pushes <c>JobId</c> onto the Serilog log context,
/// and invokes <see cref="IJob.Execute"/>.
///
/// <para>Per-tick scope keeps dependencies (DbContext, MediatR scope, ICurrentUser) cleanly
/// isolated between ticks so leaked state from one tick can never poison the next.</para>
/// </summary>
public interface IJobSafeRunner : IScopedDependency
{
    /// <summary><c>jobId</c> is the first arg so Hangfire's dashboard renders it as the job name (see <see cref="DisplayNameAttribute"/>).</summary>
    [DisplayName("{0}")]
    Task Run(string jobId, Type jobType);
}

public class JobSafeRunner : IJobSafeRunner
{
    private readonly ILifetimeScope _lifetimeScope;

    public JobSafeRunner(ILifetimeScope lifetimeScope)
    {
        _lifetimeScope = lifetimeScope;
    }

    public async Task Run(string jobId, Type jobType)
    {
        await using var newScope = _lifetimeScope.BeginLifetimeScope();

        var job = (IJob)newScope.Resolve(jobType);

        using (LogContext.PushProperty("JobId", job.JobId))
        {
            await job.Execute();
        }
    }
}
