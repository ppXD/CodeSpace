namespace CodeSpace.Messages.Dtos.Users;

/// <summary>
/// One member of a team — the minimal identity the UI needs to put a name + avatar on a
/// userId. Drives chat author rendering (messages carry only an author user id) and the
/// <c>@</c>-mention people picker. <see cref="Email"/> is included so same-name members can be
/// disambiguated in the picker, mirroring how the credential picker uses the owner's name.
/// </summary>
public sealed record TeamMemberSummary
{
    public required Guid UserId { get; init; }
    public required string Name { get; init; }
    public required string Email { get; init; }
    public string? AvatarUrl { get; init; }
}
