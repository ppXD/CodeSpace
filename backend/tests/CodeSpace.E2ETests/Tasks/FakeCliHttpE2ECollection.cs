namespace CodeSpace.E2ETests.Tasks;

/// <summary>
/// Serializes the Http-surface E2E tests that launch a REAL agent via the FAKE CLI. Each one sets the PROCESS-WIDE
/// <see cref="CodeSpace.Core.Services.Agents.Harnesses.Codex.CodexHarness.CommandEnvVar"/> (via <c>FakeCodexCli</c>) to
/// point the runner at its own <c>/bin/sh</c> script, and clears it on dispose. Run in PARALLEL (xUnit's default — one
/// class per implicit collection), one class's dispose (which UNSETS the var) or ctor (which RE-POINTS it) races another
/// class's in-flight agent spawn: that agent then resolves the WRONG / an empty command, spawns the missing real
/// <c>codex</c> binary, fails, and the launched run terminal-fails — surfacing as the intermittent
/// <c>RunStatus == Failure</c> these tests flaked on. Assigning them ONE xUnit collection runs them sequentially, so the
/// shared env var is never mutated while another class's agent is reading it. No other test in this job mutates
/// <c>CommandEnvVar</c>, so serializing these three is the complete fix. Each class keeps its own
/// <c>IClassFixture&lt;TaskLaunchApiFactory&gt;</c> (a collection assignment and a class fixture are orthogonal).
/// </summary>
[CollectionDefinition(Name)]
public sealed class FakeCliHttpE2ECollection
{
    public const string Name = "Fake CLI Http E2E (serial — shared CodexHarness.CommandEnvVar)";
}
