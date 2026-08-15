using CodeSpace.Core.Settings;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace CodeSpace.UnitTests.Settings;

[Trait("Category", "Unit")]
public sealed class ArtifactStorageQualificationTests
{
    [Fact]
    public void Local_rwx_shared_qualification_is_false_unless_the_deployment_explicitly_enables_it()
    {
        RuntimeSettings.Read(Configuration([])).ArtifactLocalRwxShared.ShouldBeFalse();
        RuntimeSettings.Read(Configuration(new() { ["Artifacts:LocalRwxShared"] = "false" })).ArtifactLocalRwxShared.ShouldBeFalse();
        RuntimeSettings.Read(Configuration(new() { ["Artifacts:LocalRwxShared"] = "true" })).ArtifactLocalRwxShared.ShouldBeTrue();
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
