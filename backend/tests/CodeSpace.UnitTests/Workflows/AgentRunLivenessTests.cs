using CodeSpace.Core.Services.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the agent-run liveness contract. The heartbeat cadence is DERIVED from the abandonment window so
/// the executor (which pings) and the reconciler (which abandons) can never drift into the
/// "window shorter than the heartbeat cadence" hole. Env-var writes are restored per test (xUnit news up
/// the class per [Fact] and Disposes after each), keeping the tests isolated.
/// </summary>
[Trait("Category", "Unit")]
public class AgentRunLivenessTests : IDisposable
{
    private readonly string? _original = Environment.GetEnvironmentVariable(AgentRunLiveness.WindowEnvVar);

    public void Dispose() => Environment.SetEnvironmentVariable(AgentRunLiveness.WindowEnvVar, _original);

    [Fact]
    public void WindowEnvVar_constant_name_is_pinned()
    {
        // Renaming this breaks every operator who pinned a custom window via env.
        AgentRunLiveness.WindowEnvVar.ShouldBe("CODESPACE_AGENT_RUN_LIVENESS_WINDOW");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-timespan")]
    public void Window_defaults_to_five_minutes_when_unset_or_unparseable(string? value)
    {
        Environment.SetEnvironmentVariable(AgentRunLiveness.WindowEnvVar, value);

        AgentRunLiveness.Window.ShouldBe(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void Window_reads_the_env_override()
    {
        Environment.SetEnvironmentVariable(AgentRunLiveness.WindowEnvVar, "00:10:00");

        AgentRunLiveness.Window.ShouldBe(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void HeartbeatInterval_is_a_third_of_the_window()
    {
        Environment.SetEnvironmentVariable(AgentRunLiveness.WindowEnvVar, "00:09:00");

        AgentRunLiveness.HeartbeatInterval.ShouldBe(TimeSpan.FromMinutes(3));
    }

    [Fact]
    public void HeartbeatInterval_is_floored_for_a_tiny_window()
    {
        // window/3 would be ~3s; the 5s floor stops the loop from busy-waiting on a tiny/forced window.
        Environment.SetEnvironmentVariable(AgentRunLiveness.WindowEnvVar, "00:00:09");

        AgentRunLiveness.HeartbeatInterval.ShouldBe(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void HeartbeatInterval_stays_below_the_window_at_the_default()
    {
        // The no-drift invariant: a live worker pings comfortably before the reconciler would abandon.
        Environment.SetEnvironmentVariable(AgentRunLiveness.WindowEnvVar, null);

        AgentRunLiveness.HeartbeatInterval.ShouldBeLessThan(AgentRunLiveness.Window);
    }
}
