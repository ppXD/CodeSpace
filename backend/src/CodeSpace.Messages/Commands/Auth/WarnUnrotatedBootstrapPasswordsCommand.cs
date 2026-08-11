using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Auth;

/// <summary>
/// Log a Warning for every user still carrying <c>password_must_change</c> — the bootstrap admin from migration 0006
/// trips this until an operator signs in and rotates. Dispatched by the recurring auditor; can also be sent ad-hoc.
///
/// <para>NOT tenant-scoped — a system-wide credential-hygiene audit with no actor context, and read-only apart from
/// the log line it emits.</para>
/// </summary>
public sealed record WarnUnrotatedBootstrapPasswordsCommand : ICommand<WarnUnrotatedBootstrapPasswordsResponse>;

/// <summary>Count of users still holding an unrotated bootstrap password — zero is the healthy state.</summary>
public sealed record WarnUnrotatedBootstrapPasswordsResponse
{
    public required int Unrotated { get; init; }
}
