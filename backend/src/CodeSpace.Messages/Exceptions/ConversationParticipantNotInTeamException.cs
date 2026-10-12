using CodeSpace.Messages.Failures;

namespace CodeSpace.Messages.Exceptions;

/// <summary>
/// A conversation was asked to hold a user who is not a member of its team.
///
/// <para>One answer for a user of another team and for an id that names nobody, on purpose: the conversation list
/// used to count only the member rows whose user existed, so writing an arbitrary id and reading the count back told
/// the caller whether that id was a real account. Telling the two refusals apart would be the same oracle.</para>
/// </summary>
public sealed class ConversationParticipantNotInTeamException : Exception, IFailure
{
    public ConversationParticipantNotInTeamException() : base("A conversation participant is not a member of the conversation's team.") { }

    public FailureKind Kind => FailureKind.NotFound;

    public string Code => FailureCodes.ConversationParticipantNotInTeam;

    public string? ClientMessage => "That person isn't a member of this team.";
}
