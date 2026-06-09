using CodeSpace.Core.Services.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

[Trait("Category", "Unit")]
public class AgentRunReconcilerServiceTests
{
    [Fact]
    public void LivenessWindowEnvVar_constant_name_is_pinned()
    {
        // Renaming this breaks any operator who tuned reclaim aggressiveness via env. Hard-pin (Rule 8).
        AgentRunReconcilerService.LivenessWindowEnvVar.ShouldBe("CODESPACE_AGENT_RUN_LIVENESS_WINDOW");
    }
}
