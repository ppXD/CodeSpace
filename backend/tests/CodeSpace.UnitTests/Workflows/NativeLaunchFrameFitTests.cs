using System.Text.Json;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents;
using CodeSpace.NativeLaunch;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// <see cref="NativeLaunchProtocol.FitsTheFrame"/> is a prediction the executor makes before the runner builds the frame,
/// and the cold degrade trusts it. Pinned here against the invocation the runner really encodes
/// (<c>StartBrokerAsync</c>: the durable start info around the frozen spec), at the boundary where the prediction flips.
/// </summary>
[Trait("Category", "Unit")]
public sealed class NativeLaunchFrameFitTests : IDisposable
{
    private readonly string _spool = Path.Combine(Path.GetTempPath(), $"cs-frame-fit-{Guid.NewGuid():N}");

    public NativeLaunchFrameFitTests() => Directory.CreateDirectory(_spool);

    public void Dispose()
    {
        try { Directory.Delete(_spool, recursive: true); } catch { /* best-effort */ }
    }

    [Theory]
    [InlineData('x')]   // an ASCII persona: its second copy costs what the first does
    [InlineData('<')]   // the worst case the web encoder allows — every character escapes to six bytes, in both copies
    public void A_spec_the_fit_admits_is_a_frame_the_pipe_carries(char personaCharacter)
    {
        // A persona on argv near the kernel's per-string ceiling, so the argv the invocation carries a second time is large.
        var persona = new string(personaCharacter, 130_000);
        var spec = SpecAtTheBoundary(persona);

        NativeLaunchProtocol.FitsTheFrame(spec).ShouldBeTrue("fixture check: the spec sits exactly at the fit boundary");
        NativeLaunchProtocol.FitsTheFrame(WithTranscript(spec, Transcript(spec) + "x")).ShouldBeFalse("fixture check: one byte more and it does not");

        var frame = EncodedFrame(spec);

        frame.ShouldBeLessThanOrEqualTo(NativeLaunchProtocol.MaximumFrameBytes, "a spec the fit admits must be a frame the runner can send, or the cold degrade is skipped and the launch refused");
        frame.ShouldBeGreaterThan(NativeLaunchProtocol.MaximumFrameBytes - 2 * NativeLaunchProtocol.InvocationAllowanceBytes, "and the fit must not give away much more than its allowance, or a continuation that fits would lose its conversation");
    }

    private SandboxSpec SpecAtTheBoundary(string persona)
    {
        var spec = new SandboxSpec
        {
            Command = "claude", WorkingDirectory = _spool, TimeoutSeconds = 900, StandardInput = "Review this change — 修复 the flaky test",
            Args = new[] { "--print", "--output-format", "stream-json", "--verbose", "--append-system-prompt", persona, "--resume", "sess-boundary" },
            Environment = new Dictionary<string, string> { ["ANTHROPIC_BASE_URL"] = "http://127.0.0.1:9", ["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1", ["GIT_AUTHOR_NAME"] = "Agent <agent@codespace>" },
            ConfigHomeEnvVars = new[] { "CLAUDE_CONFIG_DIR" },
            ConfigHomeFiles = new[] { new ConfigHomeFile { RelativePath = "projects/-ws/sess-boundary.jsonl", Content = "" } },
        };

        // The largest transcript the fit admits: each 'x' encodes to one byte, so the room left is exact.
        var room = NativeLaunchProtocol.MaximumFrameBytes - NativeLaunchProtocol.InvocationAllowanceBytes - Measured(spec);

        return WithTranscript(spec, new string('x', checked((int)room)));
    }

    private int EncodedFrame(SandboxSpec spec)
    {
        var frozen = NativeLaunchProtocol.Freeze(spec);
        var command = LocalProcessRunner.BuildDurableStartInfo(frozen, _spool, bootstrapSession: true);
        var invocation = new NativeLaunchInvocation
        {
            Spec = frozen, ReadOnlyPaths = frozen.ReadOnlyPaths, CaptureBudget = frozen.CaptureBudget,
            Command = command.FileName, Args = command.ArgumentList.ToArray(), WorkingDirectory = command.WorkingDirectory,
            Environment = command.Environment.ToDictionary(pair => pair.Key, pair => pair.Value), EgressNetnsKey = "egress-key", CgroupRunKey = "cgroup-key",
            Confinement = BubblewrapSandbox.DeriveConfinement(BubblewrapSandbox.Available, BubblewrapSandbox.UnavailableReason, shareNetwork: true, egressAllowlist: null),
        };

        return NativeLaunchFiles.EncodeFrame(invocation).Length;
    }

    private static long Measured(SandboxSpec spec) =>
        JsonSerializer.SerializeToUtf8Bytes(spec, NativeLaunchProtocol.Json).LongLength + JsonSerializer.SerializeToUtf8Bytes(spec.Args, NativeLaunchProtocol.Json).LongLength + JsonSerializer.SerializeToUtf8Bytes(spec.Environment, NativeLaunchProtocol.Json).LongLength;

    private static string Transcript(SandboxSpec spec) => spec.ConfigHomeFiles[0].Content;

    private static SandboxSpec WithTranscript(SandboxSpec spec, string transcript) => spec with { ConfigHomeFiles = new[] { spec.ConfigHomeFiles[0] with { Content = transcript } } };
}
