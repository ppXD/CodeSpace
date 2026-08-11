using Microsoft.Extensions.Logging;

namespace CodeSpace.IntegrationTests.Workflows.Supervisor;

/// <summary>
/// Surfaces a directly-constructed component's <see cref="ILogger"/> output on stdout so the CI job log carries
/// it. The real-model eval lanes construct <c>LlmSupervisorDecider</c> by hand (no DI host), so without this its
/// warnings — most importantly the RAW model reply behind an incoherent decision — are silently dropped, which is
/// exactly the observability hole that made the empty-spawn loop undiagnosable from CI (the lane's log carried
/// neither prompts nor payloads, so "what the model actually sent" was inference, not observation).
/// </summary>
internal sealed class ConsoleTestLogger<T> : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Console.WriteLine($"[{typeof(T).Name}:{logLevel}] {formatter(state, exception)}{(exception is null ? "" : $" — {exception}")}");
}
