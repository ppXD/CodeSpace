using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Shouldly;
using YamlDotNet.RepresentationModel;

namespace CodeSpace.UnitTests.Workflows;

[Trait("Category", "Unit")]
public class RealModelRouteSnapshotLaneTests
{
    [Fact]
    public void Live_route_snapshot_census_reads_results_from_its_owning_integration_assembly()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, ".github/workflows/real-model.yml"))) directory = directory.Parent;
        directory.ShouldNotBeNull();
        using var reader = File.OpenText(Path.Combine(directory.FullName, ".github/workflows/real-model.yml"));
        var yaml = new YamlStream();
        yaml.Load(reader);
        var root = (YamlMappingNode)yaml.Documents.Single().RootNode;
        var jobs = (YamlMappingNode)root.Children[new YamlScalarNode("jobs")];
        var selected = jobs.Children.Values.Cast<YamlMappingNode>().Select(job => (Job: job, Steps: ((YamlSequenceNode)job.Children[new YamlScalarNode("steps")]).Children.Cast<YamlMappingNode>().ToArray()))
            .SelectMany(job => job.Steps.Where(step => Script(step).Contains("dotnet test backend/tests/CodeSpace.IntegrationTests/CodeSpace.IntegrationTests.csproj", StringComparison.Ordinal) && Script(step).Contains("--filter \"FullyQualifiedName~RealModelPlannerRouteSnapshot\"", StringComparison.Ordinal)).Select(step => (job.Job, job.Steps, Step: step))).ToArray();
        selected.Length.ShouldBe(1, "a census over E2ETests cannot execute a test declared in IntegrationTests");
        var lane = selected.Single();
        lane.Step.Children.ContainsKey(new YamlScalarNode("continue-on-error")).ShouldBeFalse();
        var result = Regex.Match(Script(lane.Step), @"LogFileName=([^""\s;]+\.trx)");
        result.Success.ShouldBeTrue();
        var census = lane.Steps.Single(step => Script(step).Contains("assert-every-filter-clause-ran.sh", StringComparison.Ordinal) && Script(step).Contains("RealModelPlannerRouteSnapshot", StringComparison.Ordinal));
        Script(census).ShouldContain("backend/TestResults/" + result.Groups[1].Value);
        Array.IndexOf(lane.Steps, census).ShouldBeGreaterThan(Array.IndexOf(lane.Steps, lane.Step));
        var services = (YamlMappingNode)lane.Job.Children[new YamlScalarNode("services")];
        services.Children.ContainsKey(new YamlScalarNode("postgres")).ShouldBeTrue();
    }

    private static string Script(YamlMappingNode step) => step.Children.TryGetValue(new YamlScalarNode("run"), out var value) ? ((YamlScalarNode)value).Value ?? string.Empty : string.Empty;
}
