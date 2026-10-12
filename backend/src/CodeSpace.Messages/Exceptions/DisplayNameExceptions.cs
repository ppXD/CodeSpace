using CodeSpace.Messages.Failures;

namespace CodeSpace.Messages.Exceptions;

/// <summary>
/// A new account asked for a display name with '@' in it.
///
/// <para>A display name is a sign-in identifier. One shaped like an address is how an account stood in for
/// somebody else's address: sign-in matched names as well as addresses, so an account NAMED <c>victim@corp</c>
/// could be the account that address resolved to, and the victim's own password stopped working.</para>
/// </summary>
public sealed class DisplayNameLooksLikeEmailException : Exception, IFailure
{
    public DisplayNameLooksLikeEmailException() : base("A display name may not contain '@'.") { }

    public FailureKind Kind => FailureKind.Invalid;

    public string Code => FailureCodes.DisplayNameLooksLikeEmail;

    public string? ClientMessage => "A name can't contain '@'. Your email address already signs you in — choose a name people will recognise.";
}

/// <summary>
/// A new account asked for a display name another live account already signs in with, as its name or as its address.
///
/// <para>Two accounts sharing a name means the name signs in neither of them, so a duplicate is a way to take a
/// sign-in identifier away from whoever held it first. The refusal says only that the name is taken, which the
/// roster of any team that person is in already shows.</para>
///
/// <para>An accept checks the name BEFORE it looks at whether the invited address has an account, so the answer to a
/// taken name is the same either way. That also means someone who already has an account and types their own name
/// gets this refusal rather than "sign in first" — hence the message points them at signing in too.</para>
/// </summary>
public sealed class DisplayNameTakenException : Exception, IFailure
{
    public DisplayNameTakenException() : base("That display name is already used by a live account.") { }

    public FailureKind Kind => FailureKind.Conflict;

    public string Code => FailureCodes.DisplayNameTaken;

    public string? ClientMessage => "That name is already taken. Names also sign people in, so each one belongs to a single account — choose a different one. If you already have an account, sign in and open this link again instead.";
}
