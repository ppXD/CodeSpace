using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace CodeSpace.SandboxTests;

/// <summary>
/// A logger that writes a component's Warnings (and worse) into the test's own output, so a refusal the component
/// explains only in its log — a re-bind that could not take its port or re-open its socket — is readable next to the
/// assertion it failed (Rule 12.10). Output written after the test has finished is dropped rather than thrown.
/// </summary>
internal sealed class TestOutputLogger<T>(ITestOutputHelper output) : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        try { output.WriteLine($"[{typeof(T).Name} {logLevel}] {formatter(state, exception)}{(exception is null ? "" : $" ({exception.GetType().Name}: {exception.Message})")}"); }
        catch (InvalidOperationException) { /* the test is already over */ }
    }
}
