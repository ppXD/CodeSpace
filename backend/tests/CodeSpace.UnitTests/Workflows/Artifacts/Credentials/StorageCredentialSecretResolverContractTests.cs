using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Workflows.Artifacts.Credentials;
using CodeSpace.Messages.Dtos.Storage;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts.Credentials;

[Trait("Category", "Unit")]
public sealed class StorageCredentialSecretResolverContractTests
{
    [Fact]
    public void Resolver_contract_requires_one_exact_team_credential_revision_and_expected_provider()
    {
        typeof(IStorageCredentialSecretResolver).GetInterfaces().ShouldContain(typeof(IScopedDependency));
        typeof(IStorageCredentialSecretResolver).GetMethods().Select(method => method.Name).ShouldBe(["ResolveAsync"]);
        typeof(IStorageCredentialSecretResolver).GetMethod("ResolveAsync")!.GetParameters().Length.ShouldBe(2);
        typeof(StorageCredentialSecretRequest).GetProperties().Select(property => property.Name)
            .ShouldBe(["TeamId", "CredentialId", "Revision", "ExpectedProviderTypeKey"]);
    }

    [Fact]
    public void Expected_failures_are_closed_typed_values_without_secret_or_exception_text()
    {
        var failureTypes = typeof(StorageCredentialSecretResolution).GetNestedTypes()
            .Where(type => type.IsAssignableTo(typeof(StorageCredentialSecretResolution)) && type.Name != nameof(StorageCredentialSecretResolution.Ready))
            .ToList();

        failureTypes.Select(type => type.Name).OrderBy(name => name).ShouldBe([
            nameof(StorageCredentialSecretResolution.InvalidEnvelope),
            nameof(StorageCredentialSecretResolution.Missing),
            nameof(StorageCredentialSecretResolution.NotActive),
            nameof(StorageCredentialSecretResolution.ProviderMismatch),
            nameof(StorageCredentialSecretResolution.ProviderUnavailable),
            nameof(StorageCredentialSecretResolution.RevisionMissing),
        ]);
        failureTypes.SelectMany(type => type.GetProperties()).Select(property => property.Name)
            .ShouldNotContain(name => name.Contains("Secret", StringComparison.Ordinal) || name.Contains("Payload", StringComparison.Ordinal) || name.Contains("Message", StringComparison.Ordinal));
    }

    [Fact]
    public void Existing_control_plane_metadata_remains_secret_free()
    {
        typeof(StorageCredentialMetadata).GetProperties().Select(property => property.Name)
            .ShouldNotContain(name => name.Contains("Secret", StringComparison.Ordinal) || name.Contains("Payload", StringComparison.Ordinal) || name.Contains("Cipher", StringComparison.Ordinal) || name.Contains("Fingerprint", StringComparison.Ordinal));
    }

    [Fact]
    public void Ready_secret_has_no_public_property_surface_for_dto_or_structured_log_reflection()
    {
        typeof(StorageCredentialSecretResolution.Ready).GetProperties().ShouldBeEmpty();
        typeof(StorageCredentialSecretResolution.Ready).GetMethods().Where(method => method.DeclaringType == typeof(StorageCredentialSecretResolution.Ready)).Select(method => method.Name)
            .ShouldContain(nameof(StorageCredentialSecretResolution.Ready.UseSecret));
    }
}
