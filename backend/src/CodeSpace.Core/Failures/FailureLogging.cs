using CodeSpace.Messages.Failures;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Failures;

/// <summary>
/// How a failure is recorded, kept in one place so every surface that might record one agrees on the
/// severity and so exactly one of them does.
///
/// <para>Severity follows what the failure MEANS, not where it happened. A refusal is the system
/// working and belongs at Information; only a broken invariant or a dependency outage is worth waking
/// someone for, and burying those under a stream of expected 403s is how that stops working.</para>
/// </summary>
public static class FailureLogging
{
    /// <summary>
    /// Key stamped on <see cref="Exception.Data"/> once a failure has been recorded, so a later
    /// surface can tell "already logged, don't repeat it" apart from "nobody logged this at all".
    ///
    /// <para>Carried on the exception rather than in a scope or an ambient flag because the two
    /// recorders are in different layers with no shared lifetime — a MediatR exception action in
    /// Core and an MVC filter in Api — and the exception is the only thing that provably travels
    /// from one to the other.</para>
    /// </summary>
    private const string LoggedKey = "CodeSpace.FailureLogged";

    public static void MarkLogged(Exception exception) => exception.Data[LoggedKey] = true;

    public static bool WasLogged(Exception exception) => exception.Data.Contains(LoggedKey);

    /// <summary>The severity a failure of this kind is worth. See the class remarks.</summary>
    public static LogLevel SeverityFor(FailureKind kind) => kind switch
    {
        FailureKind.Internal => LogLevel.Error,
        FailureKind.Unavailable => LogLevel.Error,
        FailureKind.Conflict => LogLevel.Warning,
        FailureKind.Exhausted => LogLevel.Warning,
        _ => LogLevel.Information,
    };
}
