using System.Globalization;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts.Profiles;

[Trait("Category", "Unit")]
public sealed class StorageProfileSnapshotProjectionTests
{
    [Fact]
    public void Resolver_contract_requires_one_exact_team_profile_revision_payload()
    {
        typeof(IStorageProfileSnapshotResolver).GetMethods().Select(method => method.Name).ShouldBe(["ResolveAsync"]);
        typeof(IStorageProfileSnapshotResolver).GetMethod("ResolveAsync")!.GetParameters().Length.ShouldBe(2);
        typeof(StorageProfileSnapshotRequest).GetProperties().Select(property => property.Name).ShouldBe(["TeamId", "ProfileId", "ProfileRevision"]);
    }

    [Fact]
    public void Configuration_is_reparsed_as_an_immutable_canonical_object()
    {
        var parsed = StorageProfileSnapshotProjection.TryParseCanonicalConfiguration("""{"z":2,"nested":{"z":4,"a":3},"a":1}""", out var configuration);

        parsed.ShouldBeTrue();
        configuration.GetRawText().ShouldBe("""{"a":1,"nested":{"a":3,"z":4},"z":2}""");
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{not-json}")]
    public void Configuration_parser_fails_closed_without_throwing_for_invalid_persisted_values(string value)
    {
        StorageProfileSnapshotProjection.TryParseCanonicalConfiguration(value, out var configuration).ShouldBeFalse();
        configuration.ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Undefined);
    }

    [Fact]
    public void Database_secret_reference_is_canonical_versioned_and_culture_invariant()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-EG");
            var credentialId = Guid.Parse("11111111-2222-3333-4444-555555555555");

            var reference = StorageProfileSnapshotProjection.DatabaseSecretReference(new StorageProfileCredentialReference(credentialId, 17));

            reference.ShouldBe(new StorageSecretReference("database/v1", "11111111-2222-3333-4444-555555555555", "17"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Snapshot_and_resolution_contracts_have_no_encrypted_payload_surface()
    {
        var contractTypes = typeof(StorageProfileSnapshotResolution).Assembly.GetTypes()
            .Where(type => type == typeof(StorageProfileSnapshot) || type == typeof(StorageSecretReference) || type == typeof(StorageProfileSnapshotResolution) || type.IsNested && type.DeclaringType == typeof(StorageProfileSnapshotResolution));

        contractTypes.SelectMany(type => type.GetProperties()).Select(property => property.Name).ShouldNotContain("EncryptedPayload");
    }
}
