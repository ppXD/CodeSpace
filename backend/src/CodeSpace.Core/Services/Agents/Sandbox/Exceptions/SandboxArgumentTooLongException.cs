using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Agents.Sandbox.Exceptions;

/// <summary>
/// The invocation cannot be executed as written: one of its argv or environment strings is past the kernel's
/// per-string ceiling. Its own exit scenario rather than a <see cref="NativeLaunchException"/> reason, because the two
/// differ in the one way a caller acts on. A launch-slot refusal is about THIS host — a foreign boot, a binding
/// conflict, a missing bootstrap — and another worker may well admit it, so it stays retryable. These bytes are
/// refused by every kernel on every host, so a retry is N identical refusals, each one burning budget and burying the
/// one fact the author needs.
///
/// <para>The message is host metadata — a size, a limit, a position. An environment entry is named by its variable
/// only: a value can be a credential.</para>
/// </summary>
public sealed class SandboxArgumentTooLongException(string message) : Exception(message), IFailure
{
    public FailureKind Kind => FailureKind.Unprocessable;
    public string Code => FailureCodes.SandboxArgumentTooLong;
}
