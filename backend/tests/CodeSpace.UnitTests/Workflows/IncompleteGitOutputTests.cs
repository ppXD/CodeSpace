using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Exceptions;
using CodeSpace.Core.Services.Agents.Workspace.Providers;
using CodeSpace.Messages.Agents;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

[Trait("Category", "Unit")]
[Collection("WorkspaceProvisioning")]
public sealed class IncompleteGitOutputTests
{
    [Fact]
    public async Task Incomplete_base_revision_is_never_parsed_as_a_full_sha()
    {
        var runner = new OutputRunner(spec => spec.Args.Contains("rev-parse") ? Incomplete("abc") : Success());
        var error = await Should.ThrowAsync<IncompleteSandboxOutputException>(() => Provider(runner).PrepareAsync(Request(), CancellationToken.None));
        error.Result.Status.ShouldBe(SandboxStatus.Success);
        error.Result.Stdout.ShouldBe("abc");
        error.Stream.ShouldBe("stdout");
        runner.Calls.Count(spec => spec.Args.Contains("clone")).ShouldBe(1);
    }

    [Fact]
    public async Task Incomplete_diff_is_rejected_without_repeating_the_prior_stage_command()
    {
        var runner = new OutputRunner(spec => spec.Args.Contains("diff") ? Incomplete("diff prefix") : Success());
        await using var handle = await Provider(runner).PrepareAsync(Request(), CancellationToken.None);
        var error = await Should.ThrowAsync<IncompleteSandboxOutputException>(() => handle.CaptureChangesAsync(CancellationToken.None));
        error.Result.Stdout.ShouldBe("diff prefix");
        runner.Calls.Count(spec => spec.Args.Contains("add")).ShouldBe(1);
        runner.Calls.Count(spec => spec.Args.Contains("diff")).ShouldBe(1);
    }

    [Fact]
    public async Task Incomplete_commit_failure_is_not_misclassified_from_a_matching_prefix()
    {
        var failure = Incomplete("nothing to commit") with { Status = SandboxStatus.Failed, ExitCode = 128 };
        var runner = new OutputRunner(spec => spec.Args.Contains("commit") ? failure : Success());
        await using var handle = await Provider(runner).PrepareAsync(Request(), CancellationToken.None);
        var error = await Should.ThrowAsync<IncompleteSandboxOutputException>(() => ((CodeSpace.Core.Services.Agents.Workspace.IWorkspacePushHandle)handle).PushChangesAsync("test-branch", CancellationToken.None));
        error.Result.ShouldBeSameAs(failure);
        runner.Calls.Count(spec => spec.Args.Contains("commit")).ShouldBe(1);
        runner.Calls.ShouldNotContain(spec => spec.Args.Contains("push"));
    }

    [Fact]
    public async Task Incomplete_push_readback_withholds_confirmation_without_replaying_a_successful_push()
    {
        var runner = new OutputRunner(spec => spec.Args.Contains("ls-remote") ? Incomplete(new string('a', 40)) : Success());
        await using var handle = await Provider(runner).PrepareAsync(Request(), CancellationToken.None);
        var push = (CodeSpace.Core.Services.Agents.Workspace.IWorkspacePushHandle)handle;
        (await push.PushChangesAsync("test-branch", CancellationToken.None)).ShouldBe("test-branch");
        push.LastPushedCommitSha().ShouldBeNull();
        runner.Calls.Count(spec => spec.Args.Contains("push")).ShouldBe(1);
        runner.Calls.Count(spec => spec.Args.Contains("ls-remote")).ShouldBe(1);
    }

    private static SandboxResult Success() => new() { Status = SandboxStatus.Success, ExitCode = 0, Stdout = new string('a', 40), Stderr = "" };
    private static SandboxResult Incomplete(string text) => Success() with { Stdout = text, Observation = new SandboxObservation { Stdout = new SandboxStreamObservation { ObservedBytes = 100, ReachedEndOfStream = true, CaptureComplete = false } } };
    private static WorkspaceProvisionRequest Request() => WorkspaceProvisionRequest.FromSingle(new WorkspaceRequest { RepositoryUrl = "https://example.test/repo.git", Token = "fixture" });
    private static LocalGitWorkspaceProvider Provider(OutputRunner runner) => new(new SandboxRunnerRegistry(new[] { runner }), NullLogger<LocalGitWorkspaceProvider>.Instance);

    private sealed class OutputRunner(Func<SandboxSpec, SandboxResult> reply) : ISandboxRunner
    {
        public string Kind => "local";
        public List<SandboxSpec> Calls { get; } = new();
        public Task<SandboxResult> RunAsync(SandboxSpec spec, CancellationToken cancellationToken) { Calls.Add(spec); return Task.FromResult(reply(spec)); }
    }
}
