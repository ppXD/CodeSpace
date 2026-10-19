using CodeSpace.Core.Authorization;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Credentials;

/// <summary>
/// Who a credential belongs to, as the rules about acting on it see them — built once, from the row and what the team says about its
/// person, by everything that decides a repair: the provider remedy, the credential list and (through <see cref="CredentialOwnerOrPermission"/>)
/// the OAuth callback. Pure, so the shapes are pinned here.
/// </summary>
[Trait("Category", "Unit")]
public class CredentialPersonTests
{
    private static readonly Guid Caller = Guid.NewGuid();
    private static readonly Guid Nacho = Guid.NewGuid();

    [Fact]
    public void A_team_service_credential_has_no_person()
    {
        CredentialPerson.Of(CredentialOwnership.TeamService, ownerUserId: null, Caller, new Dictionary<Guid, TeamRole>()).ShouldBeNull();
    }

    [Fact]
    public void A_personal_credential_stored_with_no_owner_has_no_person_to_ask_about()
    {
        CredentialPerson.Of(CredentialOwnership.Personal, ownerUserId: null, Caller, new Dictionary<Guid, TeamRole>()).ShouldBeNull();
    }

    [Fact]
    public void A_personal_credential_is_its_persons_and_carries_the_role_they_stand_as()
    {
        var person = CredentialPerson.Of(CredentialOwnership.Personal, Nacho, Caller, new Dictionary<Guid, TeamRole> { [Nacho] = TeamRole.Admin })!;

        person.IsCaller.ShouldBeFalse();
        person.Standing.ShouldBe(TeamRole.Admin);
    }

    [Fact]
    public void The_caller_is_the_person_of_their_own_credential()
    {
        CredentialPerson.Of(CredentialOwnership.Personal, Caller, Caller, new Dictionary<Guid, TeamRole> { [Caller] = TeamRole.Viewer })!.IsCaller.ShouldBeTrue();
    }

    [Fact]
    public void A_person_the_team_knows_nothing_about_has_left_and_stands_as_no_one()
    {
        CredentialPerson.Of(CredentialOwnership.Personal, Nacho, Caller, new Dictionary<Guid, TeamRole> { [Guid.NewGuid()] = TeamRole.Member })!.Standing.ShouldBeNull();
    }

    [Fact]
    public void Nobody_is_the_caller_of_a_request_that_has_no_caller()
    {
        CredentialPerson.Of(CredentialOwnership.Personal, Nacho, callerId: null, new Dictionary<Guid, TeamRole> { [Nacho] = TeamRole.Member })!.IsCaller.ShouldBeFalse();
    }
}
