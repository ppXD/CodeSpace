using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Messages.Dtos.Credentials;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Credentials;

/// <summary>
/// When a disconnected credential may be taken out of the team's list: it is disconnected, and nothing leans on it. Pure, so every
/// shape the Providers dialog could send a removal for is pinned without a database — and the words the refusal gives the person.
/// </summary>
[Trait("Category", "Unit")]
public class CredentialRemovalTests
{
    [Fact]
    public void A_disconnected_credential_with_nothing_on_it_may_be_removed()
    {
        CredentialRemoval.Refusal(Credential(CredentialStatus.Revoked), Leaning()).ShouldBeNull();
    }

    [Theory]
    [InlineData(CredentialStatus.Active)]
    [InlineData(CredentialStatus.Error)]
    [InlineData(CredentialStatus.Expired)]
    public void A_credential_that_is_still_in_service_is_never_removed(CredentialStatus status)
    {
        var refusal = CredentialRemoval.Refusal(Credential(status), Leaning());

        refusal.ShouldNotBeNull();
        refusal.ShouldContain("still connected", Case.Sensitive);
        refusal.ShouldContain("Disconnect it first", Case.Sensitive);
    }

    [Theory]
    [InlineData(1, 0, 0, "1 repository")]
    [InlineData(3, 0, 0, "3 repositories")]
    [InlineData(0, 1, 0, "1 group hook")]
    [InlineData(0, 2, 0, "2 group hooks")]
    [InlineData(0, 0, 1, "1 act-as identity")]
    [InlineData(0, 0, 2, "2 act-as identities")]
    [InlineData(2, 1, 1, "2 repositories, 1 group hook and 1 act-as identity")]
    [InlineData(1, 0, 4, "1 repository and 4 act-as identities")]
    public void A_disconnected_credential_something_leans_on_is_refused_and_says_what(int repositories, int hooks, int identities, string expected)
    {
        var refusal = CredentialRemoval.Refusal(Credential(CredentialStatus.Revoked), Leaning(repositories, hooks, identities));

        refusal.ShouldNotBeNull();
        refusal.ShouldContain(expected, Case.Sensitive);
        refusal.ShouldContain("Move or unlink them first", Case.Sensitive);
    }

    [Fact]
    public void The_refusal_names_the_credential()
    {
        CredentialRemoval.Refusal(Credential(CredentialStatus.Active, "Maya's GitLab"), Leaning()).ShouldContain("'Maya's GitLab'", Case.Sensitive);
        CredentialRemoval.Refusal(Credential(CredentialStatus.Revoked, "Maya's GitLab"), Leaning(1, 0, 0)).ShouldContain("'Maya's GitLab'", Case.Sensitive);
    }

    private static Credential Credential(CredentialStatus status, string name = "Maya's GitLab") => new() { Id = Guid.NewGuid(), TeamId = Guid.NewGuid(), ProviderInstanceId = Guid.NewGuid(), DisplayName = name, Status = status };

    private static CredentialDependents Leaning(int repositories = 0, int hooks = 0, int identities = 0) => new()
    {
        CredentialId = Guid.NewGuid(),
        DisplayName = "irrelevant",
        Status = CredentialStatus.Revoked,
        Retired = false,
        ProviderInstanceId = Guid.NewGuid(),
        Repositories = Enumerable.Range(0, repositories).Select(i => new DependentRepository { Id = Guid.NewGuid(), FullPath = $"acme/r{i}", Status = RepositoryStatus.Error, Projects = new List<DependentProject>() }).ToList(),
        ConnectionWebhooks = Enumerable.Range(0, hooks).Select(i => new DependentConnectionWebhook { Id = Guid.NewGuid(), OwnerPath = $"acme/g{i}", RegistrationStatus = RepositoryWebhookRegistrationStatus.Registered }).ToList(),
        Identities = Enumerable.Range(0, identities).Select(i => new DependentIdentity { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), ProviderUsername = $"u{i}" }).ToList(),
        Activations = new List<DependentActivation>()
    };
}
