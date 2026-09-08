using System.Text.RegularExpressions;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>Pins the cross-job admission waves that keep one shared real-model gateway measurable under load.</summary>
[Trait("Category", "Unit")]
public sealed class RealModelGatewayAdmissionLaneTests
{
    [Fact]
    public void Expensive_gateway_lanes_run_in_bounded_waves_and_continue_after_an_upstream_verdict()
    {
        var workflow = File.ReadAllText(LocateWorkflow());

        Job(workflow, "real-model").ShouldContain("needs: [real-model-benchmark, real-model-injection, real-model-publish-manifest, real-model-footer-signals, real-model-stop-hook]");
        Job(workflow, "real-model").ShouldContain("if: ${{ always() }}", customMessage: "a failed capability verdict must not silently de-select later measurement lanes");
        Job(workflow, "real-model-benchmark-extended").ShouldContain("needs: real-model");
        Job(workflow, "real-model-benchmark-extended").ShouldContain("always() && github.event_name == 'workflow_dispatch'");
        Job(workflow, "real-model-qualification-rehearsal").ShouldContain("needs: real-model-benchmark-extended");
        Job(workflow, "real-model-qualification-rehearsal").ShouldContain("always() && github.event_name == 'workflow_dispatch'");
        Job(workflow, "real-model-wholeloop-aux").ShouldContain("needs: [real-model, real-model-benchmark-extended, real-model-qualification-rehearsal]");
        Job(workflow, "real-model-wholeloop-aux").ShouldContain("if: ${{ always() }}");
        Job(workflow, "real-model-wholeloop").ShouldContain("needs: real-model-wholeloop-aux");
        Job(workflow, "real-model-wholeloop").ShouldContain("if: ${{ always() }}");
    }

    private static string Job(string workflow, string name)
    {
        var start = workflow.IndexOf($"\n  {name}:\n", StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"job '{name}' must exist");
        var bodyStart = start + name.Length + 5;
        var next = Regex.Match(workflow[bodyStart..], @"(?m)^  [a-zA-Z0-9_-]+:\s*$");
        return next.Success ? workflow[bodyStart..(bodyStart + next.Index)] : workflow[bodyStart..];
    }

    private static string LocateWorkflow()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, ".github/workflows/real-model.yml"))) directory = directory.Parent;
        return Path.Combine(directory?.FullName ?? throw new DirectoryNotFoundException("repository root not found"), ".github/workflows/real-model.yml");
    }
}
