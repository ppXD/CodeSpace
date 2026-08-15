using YamlDotNet.RepresentationModel;
using Shouldly;

namespace CodeSpace.UnitTests.Settings;

/// <summary>
/// The process spool is the recoverable source of truth until its redacted streams reach durable artifact CAS.
/// Preparing the directory in an image is not persistence: a worker replacement must see the same bytes.
/// </summary>
[Trait("Category", "Unit")]
public class DurableStorageTopologyTests
{
    [Theory]
    [InlineData("docker-compose.yml", "codespace-spool")]
    [InlineData("backend/deploy/e2e/docker-compose.e2e.yml", "spool")]
    public void Worker_spool_is_a_declared_named_volume(string relativePath, string volumeName)
    {
        var root = Mapping(Load(relativePath));
        var services = Mapping(root.Children[Scalar("services")]);
        var worker = Mapping(services.Children[Scalar("worker")]);
        var mounts = Sequence(worker.Children[Scalar("volumes")]).Children.Select(value => ScalarValue(value)).ToArray();
        var volumes = Mapping(root.Children[Scalar("volumes")]);

        mounts.ShouldContain($"{volumeName}:{CodeSpace.Core.Settings.DurableRoots.ContainerAgentRunSpool}");
        volumes.Children.Keys.Select(ScalarValue).ShouldContain(volumeName);
    }

    [Theory]
    [InlineData("docker-compose.yml", "codespace-artifacts")]
    [InlineData("backend/deploy/e2e/docker-compose.e2e.yml", "artifacts")]
    public void Local_rwx_is_qualified_only_in_stacks_where_api_and_worker_share_one_declared_artifact_volume(string relativePath, string volumeName)
    {
        var root = Mapping(Load(relativePath));
        var services = Mapping(root.Children[Scalar("services")]);
        var volumes = Mapping(root.Children[Scalar("volumes")]);

        foreach (var serviceName in new[] { "api", "worker" })
        {
            var service = Mapping(services.Children[Scalar(serviceName)]);
            var environment = Mapping(service.Children[Scalar("environment")]);
            var mounts = Sequence(service.Children[Scalar("volumes")]).Children.Select(ScalarValue).ToArray();

            ScalarValue(environment.Children[Scalar("Artifacts__LocalRwxShared")]).ShouldBe("true");
            mounts.ShouldContain($"{volumeName}:{CodeSpace.Core.Settings.DurableRoots.ContainerArtifactStore}");
        }

        volumes.Children.Keys.Select(ScalarValue).ShouldContain(volumeName);
    }

    private static YamlNode Load(string relativePath)
    {
        using var reader = File.OpenText(Path.Combine(FindRepoRoot(), relativePath));
        var yaml = new YamlStream();
        yaml.Load(reader);
        return yaml.Documents.Single().RootNode;
    }

    private static YamlMappingNode Mapping(YamlNode node) => node.ShouldBeOfType<YamlMappingNode>();
    private static YamlSequenceNode Sequence(YamlNode node) => node.ShouldBeOfType<YamlSequenceNode>();
    private static YamlScalarNode Scalar(string value) => new(value);
    private static string ScalarValue(YamlNode node) => node.ShouldBeOfType<YamlScalarNode>().Value!;

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "backend"))) return dir.FullName;
        throw new InvalidOperationException("repo root not found");
    }
}
