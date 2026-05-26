using CodeSpace.Core.DependencyInjection;

namespace CodeSpace.Core.Jobs;

/// <summary>
/// Background job descriptor. <see cref="IJobSafeRunner"/> resolves the concrete type from
/// DI per tick + invokes <see cref="Execute"/> inside a fresh lifetime scope with
/// <c>JobId</c> pushed into the Serilog log context.
/// </summary>
public interface IJob : IScopedDependency
{
    /// <summary>
    /// Stable id Hangfire indexes the job by. <see cref="nameof"/> of the class is the
    /// standard convention so a rename = new id + old one stays dead in Hangfire.
    /// </summary>
    string JobId { get; }

    Task Execute();
}
