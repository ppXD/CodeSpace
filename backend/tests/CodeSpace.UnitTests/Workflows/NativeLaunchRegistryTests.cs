using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using CodeSpace.Core.Services.Agents.Sandbox.Exceptions;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents;
using CodeSpace.NativeLaunch;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

[Trait("Category", "Unit")]
[Collection("LocalProcessIdleWatchdog")]
public sealed class NativeLaunchRegistryTests
{
    [Fact]
    public void Canonical_binding_preserves_exact_argv_and_all_server_only_fields()
    {
        var args = new[] { "", "a b", "雪🙂", "--literal" };
        var environment = new Dictionary<string, string> { ["SECOND"] = "two", ["FIRST"] = "secret" };
        var readOnly = new[] { "/host/readable" };
        var spec = new SandboxSpec { Command = "/bin/sh", Args = args, Environment = environment, ReadOnlyPaths = readOnly, CaptureBudget = new() { StdoutBytes = 123 } };
        var frozen = NativeLaunchProtocol.Freeze(spec);
        var hash = NativeLaunchProtocol.SpecHash(frozen);
        args[0] = "changed"; environment["FIRST"] = "rotated"; readOnly[0] = "/widened";
        NativeLaunchProtocol.SpecHash(frozen).ShouldBe(hash);
        frozen.Args.ShouldBe(new[] { "", "a b", "雪🙂", "--literal" });
        NativeLaunchProtocol.SpecHash(frozen with { Environment = new Dictionary<string, string> { ["FIRST"] = "secret", ["SECOND"] = "two" } }).ShouldBe(hash);
        NativeLaunchProtocol.SpecHash(frozen with { Args = ["a b", "雪🙂", "--literal"] }).ShouldNotBe(hash);
        NativeLaunchProtocol.SpecHash(frozen with { ReadOnlyPaths = [] }).ShouldNotBe(hash);
        NativeLaunchProtocol.SpecHash(frozen with { CaptureBudget = new() { StdoutBytes = 124 } }).ShouldNotBe(hash);
        NativeLaunchProtocol.SpecHash(frozen with { Environment = environment }).ShouldNotBe(hash);
    }

    [Fact]
    public async Task Concurrent_real_OS_observers_share_one_physical_execution_and_the_same_receipt()
    {
        await using var fixture = new Fixture();
        var workers = Enumerable.Range(0, 4).Select(_ => fixture.Observer(crash: false)).ToArray();
        await File.WriteAllTextAsync(fixture.Barrier, "release");
        var handles = await Task.WhenAll(workers.Select(worker => fixture.ReadObserverAsync(worker)));
        handles.Select(handle => handle.ProcessId).Distinct().Count().ShouldBe(1);
        handles.Select(handle => handle.ProcessStartTimeUtc).Distinct().Count().ShouldBe(1);
        handles.Select(handle => handle.Deadline).Distinct().Count().ShouldBe(1);
        await fixture.WaitAsync(() => fixture.StartCount == 1);
        await Task.Delay(200);
        fixture.StartCount.ShouldBe(1);
        fixture.ReadReceipt().Execution!.ProcessId.ShouldBe(handles[0].ProcessId);
    }

    [Fact]
    public async Task Observer_process_exit_before_application_handle_persistence_is_discoverable_and_does_not_extend_deadline()
    {
        await using var fixture = new Fixture(timeout: 2);
        using var worker = fixture.Observer(crash: true);
        await File.WriteAllTextAsync(fixture.Barrier, "release");
        var first = await fixture.ReadObserverAsync(worker, expectedExit: 86);
        await fixture.WaitAsync(() => fixture.StartCount == 1);
        var replay = await fixture.LaunchAsync();
        replay.ProcessId.ShouldBe(first.ProcessId);
        replay.Deadline.ShouldBe(first.Deadline);
        await fixture.WaitAsync(() => fixture.StopReason() == "deadline");
        await fixture.WaitAsync(() => !NativeProcess.IsAlive(fixture.ReadReceipt().Execution!));
        fixture.StartCount.ShouldBe(1);
        var result = await fixture.Runner.AttachAsync(replay, (_, _) => Task.CompletedTask, fixture.Token);
        result.Status.ShouldBe(SandboxStatus.TimedOut);
    }

    [Theory]
    [InlineData("broker")]
    [InlineData("guardian")]
    public async Task Losing_a_controller_stops_the_workload_without_replaying_its_committed_slot(string controller)
    {
        await using var fixture = new Fixture();
        var handle = await fixture.LaunchAsync();
        await fixture.WaitAsync(() => fixture.StartCount == 1);
        var receipt = fixture.ReadReceipt();
        var identity = controller == "broker" ? receipt.Broker : receipt.Guardian!;
        using var process = Process.GetProcessById(identity.ProcessId);
        process.Kill(entireProcessTree: false);
        await fixture.WaitAsync(() => fixture.StopReason() == controller + "-lost");
        await fixture.WaitAsync(() => !NativeProcess.IsAlive(receipt.Execution!));
        var replay = await fixture.LaunchAsync();
        replay.ProcessId.ShouldBe(handle.ProcessId);
        fixture.StartCount.ShouldBe(1);
    }

    [Theory]
    [InlineData("boot")]
    [InlineData("birth")]
    public async Task Native_controller_kill_refuses_a_different_kernel_identity_even_with_the_same_PID_and_wall_time(string changed)
    {
        await using var fixture = new Fixture();
        await fixture.LaunchAsync();
        await fixture.WaitAsync(() => fixture.StartCount == 1);
        var actual = fixture.ReadReceipt().Execution!;
        var stale = changed == "boot" ? actual with { BootId = "previous-boot" } : actual with { StartKey = actual.StartKey + "-different" };
        NativeProcess.IsAlive(stale).ShouldBeFalse();
        NativeProcess.KillSession(stale);
        NativeProcess.IsAlive(actual).ShouldBeTrue("wall time equality must never substitute for the recorded kernel birth identity");
    }

    [Theory]
    [InlineData("argv")]
    [InlineData("environment")]
    [InlineData("readonly")]
    [InlineData("capture")]
    [InlineData("identity")]
    public async Task Same_slot_with_a_different_immutable_request_is_rejected(string changed)
    {
        await using var fixture = new Fixture();
        await fixture.LaunchAsync();
        var original = fixture.Request;
        var changedRequest = changed switch
        {
            "argv" => original with { Spec = original.Spec with { Args = original.Spec.Args.Append("").ToArray() } },
            "environment" => original with { Spec = original.Spec with { Environment = new Dictionary<string, string> { ["KEY"] = "rotated" } } },
            "readonly" => original with { Spec = original.Spec with { ReadOnlyPaths = [fixture.Root] } },
            "capture" => original with { Spec = original.Spec with { CaptureBudget = new() } },
            _ => original with { Identity = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()) },
        };
        var error = await Should.ThrowAsync<NativeLaunchException>(() => fixture.Runner.LaunchOrDiscoverAsync(changedRequest, fixture.Token));
        error.Reason.ShouldBe("binding-conflict");
        await fixture.WaitAsync(() => fixture.StartCount == 1);
        fixture.StartCount.ShouldBe(1);
    }

    [Fact]
    public async Task Exact_empty_whitespace_and_unicode_arguments_reach_the_real_process_and_receipts_do_not_contain_secrets()
    {
        await using var fixture = new Fixture();
        var secret = "private-env-" + Guid.NewGuid().ToString("N");
        var spec = fixture.Request.Spec with { Command = "/usr/bin/printf", Args = ["<%s>\n", "", "a b", "雪🙂"], Environment = new Dictionary<string, string> { ["TEST_PRIVATE_KEY"] = secret } };
        var handle = await fixture.Runner.LaunchOrDiscoverAsync(fixture.Request with { Spec = spec }, fixture.Token);
        var result = await fixture.Runner.AttachAsync(handle, (_, _) => Task.CompletedTask, fixture.Token);
        result.Status.ShouldBe(SandboxStatus.Success);
        (await File.ReadAllTextAsync(Path.Combine(fixture.Spool, "out.log"))).ShouldBe("<>\n<a b>\n<雪🙂>\n");
        foreach (var file in Directory.GetFiles(fixture.Directory, "*.json")) (await File.ReadAllTextAsync(file)).ShouldNotContain(secret);
    }

    [Theory]
    [InlineData("request.json")]
    [InlineData("receipt.json")]
    public async Task Required_metadata_write_failure_prevents_any_command_execution(string blockedFile)
    {
        await using var fixture = new Fixture();
        Directory.CreateDirectory(Path.Combine(fixture.Directory, blockedFile));
        await Should.ThrowAsync<Exception>(() => fixture.LaunchAsync());
        fixture.StartCount.ShouldBe(0);
        File.Exists(Path.Combine(fixture.Directory, NativeLaunchProtocol.ExecutionFile)).ShouldBeFalse();
        if (blockedFile == NativeLaunchProtocol.ReceiptFile)
        {
            Directory.Delete(Path.Combine(fixture.Directory, blockedFile));
            using var retryDeadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            await Should.ThrowAsync<Exception>(() => fixture.Runner.LaunchOrDiscoverAsync(fixture.Request, retryDeadline.Token));
            fixture.StartCount.ShouldBe(0);
        }
    }

    [Fact]
    public async Task Ready_receipt_write_failure_after_bootstrap_identity_still_prevents_exec()
    {
        await using var fixture = new Fixture();
        fixture.Bind();
        using var broker = fixture.Broker();
        (await broker.StandardOutput.ReadLineAsync(fixture.Token)).ShouldBe("owned");
        File.Delete(Path.Combine(fixture.Directory, NativeLaunchProtocol.ReceiptFile));
        Directory.CreateDirectory(Path.Combine(fixture.Directory, NativeLaunchProtocol.ReceiptFile));
        await NativeLaunchFiles.WriteFrameAsync(broker.StandardInput.BaseStream, fixture.Invocation(), fixture.Token);
        broker.StandardInput.Close();
        await broker.WaitForExitAsync(fixture.Token);
        broker.ExitCode.ShouldBe(125);
        File.Exists(Path.Combine(fixture.Directory, NativeLaunchProtocol.ExecutionFile)).ShouldBeTrue("a real bootstrap reached its release barrier");
        File.Exists(Path.Combine(fixture.Directory, NativeLaunchProtocol.GuardianFile)).ShouldBeTrue();
        fixture.StartCount.ShouldBe(0, "a ready-receipt failure must not release the bootstrap into exec");
        var identity = NativeLaunchFiles.Read<NativeProcessIdentity>(fixture.Directory, NativeLaunchProtocol.ExecutionFile);
        await fixture.WaitAsync(() => !NativeProcess.IsAlive(identity));
    }

    [Fact]
    public async Task Missing_bundled_host_in_an_independent_observer_does_not_fall_back_to_unsafe_launch()
    {
        await using var fixture = new Fixture();
        using var observer = fixture.Observer(crash: false, missingHost: true);
        await File.WriteAllTextAsync(fixture.Barrier, "release");
        var error = await observer.StandardError.ReadToEndAsync(fixture.Token);
        await observer.WaitForExitAsync(fixture.Token);
        observer.ExitCode.ShouldNotBe(0);
        error.ShouldContain("bundled native runner host is unavailable");
        fixture.StartCount.ShouldBe(0);
        File.Exists(Path.Combine(fixture.Directory, NativeLaunchProtocol.CommitmentFile)).ShouldBeFalse();
    }

    [Fact]
    public async Task Incomplete_private_pipe_does_not_release_and_the_consumed_commitment_cannot_be_replayed()
    {
        await using var fixture = new Fixture();
        fixture.Bind();
        using var broker = fixture.Broker();
        (await broker.StandardOutput.ReadLineAsync(fixture.Token)).ShouldBe("owned");
        var size = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(size, 4096);
        await broker.StandardInput.BaseStream.WriteAsync(size, fixture.Token);
        await broker.StandardInput.BaseStream.WriteAsync("{"u8.ToArray(), fixture.Token);
        broker.StandardInput.Close();
        await broker.WaitForExitAsync(fixture.Token);
        broker.ExitCode.ShouldBe(125);
        fixture.ReadReceipt().State.ShouldBe("rejected");
        var error = await Should.ThrowAsync<NativeLaunchException>(() => fixture.LaunchAsync());
        error.Reason.ShouldBe("indeterminate");
        fixture.StartCount.ShouldBe(0);
    }

    [Fact]
    public async Task Bootstrap_crash_after_commitment_before_full_input_is_indeterminate_and_never_reexecutes()
    {
        await using var fixture = new Fixture();
        fixture.Bind();
        using var broker = fixture.Broker();
        (await broker.StandardOutput.ReadLineAsync(fixture.Token)).ShouldBe("owned");
        broker.Kill(entireProcessTree: false);
        await broker.WaitForExitAsync(fixture.Token);
        var error = await Should.ThrowAsync<NativeLaunchException>(() => fixture.LaunchAsync());
        error.Reason.ShouldBe("indeterminate");
        fixture.StartCount.ShouldBe(0);
    }

    [Theory]
    [InlineData("{\"specHash\":")]
    [InlineData("null")]
    public async Task Corrupt_committed_receipt_is_typed_indeterminate_and_cannot_start_another_execution(string corrupt)
    {
        await using var fixture = new Fixture();
        await fixture.LaunchAsync();
        await fixture.WaitAsync(() => fixture.StartCount == 1);
        var path = Path.Combine(fixture.Directory, NativeLaunchProtocol.ReceiptFile);
        var original = await File.ReadAllTextAsync(path);
        try
        {
            await File.WriteAllTextAsync(path, corrupt);
            var error = await Should.ThrowAsync<NativeLaunchException>(() => fixture.LaunchAsync());
            error.Reason.ShouldBe("indeterminate");
            fixture.StartCount.ShouldBe(1);
        }
        finally { await File.WriteAllTextAsync(path, original); }
    }

    [Fact]
    public async Task Duplicate_bootstraps_cannot_both_consume_the_start_commitment()
    {
        await using var fixture = new Fixture();
        fixture.Bind();
        using var first = fixture.Broker();
        using var second = fixture.Broker();
        var replies = await Task.WhenAll(first.StandardOutput.ReadLineAsync(fixture.Token).AsTask(), second.StandardOutput.ReadLineAsync(fixture.Token).AsTask());
        replies.OrderBy(value => value).ShouldBe(new[] { "discover", "owned" });
        first.StandardInput.Close(); second.StandardInput.Close();
        await Task.WhenAll(first.WaitForExitAsync(fixture.Token), second.WaitForExitAsync(fixture.Token));
        fixture.StartCount.ShouldBe(0);
        new[] { first.ExitCode, second.ExitCode }.OrderBy(value => value).ShouldBe(new[] { 0, 125 });
    }

    [Fact]
    public async Task Precommit_cancellation_does_not_create_a_launch_request()
    {
        await using var fixture = new Fixture();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => fixture.Runner.LaunchOrDiscoverAsync(fixture.Request, cancelled.Token));
        File.Exists(Path.Combine(fixture.Directory, NativeLaunchProtocol.RequestFile)).ShouldBeFalse();
        fixture.StartCount.ShouldBe(0);
    }

    [Fact]
    public async Task Cancelling_observation_does_not_cancel_the_committed_execution()
    {
        await using var fixture = new Fixture();
        var handle = await fixture.LaunchAsync();
        await fixture.WaitAsync(() => fixture.StartCount == 1);
        using var observing = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Should.ThrowAsync<OperationCanceledException>(() => fixture.Runner.AttachAsync(handle, (_, _) => Task.CompletedTask, observing.Token));
        NativeProcess.IsAlive(fixture.ReadReceipt().Execution!).ShouldBeTrue();
        await File.WriteAllTextAsync(fixture.Release, "release");
        var result = await fixture.Runner.AttachAsync(handle, (_, _) => Task.CompletedTask, fixture.Token);
        result.Status.ShouldBe(SandboxStatus.Success);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(15));
        private readonly List<Process> _workers = [];
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "cs-native-launch-" + Guid.NewGuid().ToString("N"));
        public string Key { get; } = "native-launch-" + Guid.NewGuid().ToString("N");
        public string Spool => LocalProcessRunner.SpoolDirectoryFor(Key);
        public string Directory => NativeLaunchFiles.DirectoryFor(Spool);
        public string Starts => Path.Combine(Root, "starts");
        public string Release => Path.Combine(Root, "release");
        public string Barrier => Path.Combine(Root, "observer-release");
        public CancellationToken Token => _deadline.Token;
        public LocalProcessRunner Runner { get; } = new();
        public SandboxLaunchRequest Request { get; }
        public int StartCount => File.Exists(Starts) ? File.ReadAllLines(Starts).Length : 0;

        public Fixture(int timeout = 10)
        {
            System.IO.Directory.CreateDirectory(Root); System.IO.Directory.CreateDirectory(Directory);
            Request = new SandboxLaunchRequest(new SandboxSpec { Command = "/bin/sh", Args = ["-c", "printf '%s\\n' \"$$\" >>\"$1\"; while [ ! -f \"$2\" ]; do sleep 0.02; done", "native-launch", Starts, Release], WorkingDirectory = Root, TimeoutSeconds = timeout }, Key);
        }

        public Task<SandboxHandle> LaunchAsync() => Runner.LaunchOrDiscoverAsync(Request, Token);
        public NativeLaunchReceipt ReadReceipt() => NativeLaunchFiles.Read<NativeLaunchReceipt>(Directory, NativeLaunchProtocol.ReceiptFile);
        public string? StopReason()
        {
            try { return NativeLaunchFiles.Read<NativeLaunchStop>(Directory, NativeLaunchProtocol.StopFile).Reason; }
            catch (FileNotFoundException) { return null; }
            catch (JsonException) { return null; }
        }

        public void Bind()
        {
            var now = DateTimeOffset.UtcNow;
            NativeLaunchFiles.TryCreate(Directory, NativeLaunchProtocol.RequestFile, new NativeLaunchRecord { SpecHash = NativeLaunchProtocol.SpecHash(Request.Spec), SpoolKey = Key, Host = LocalProcessRunner.CurrentHost, BootId = NativeProcess.BootId, CreatedAt = now, Deadline = now.AddSeconds(10) }).ShouldBeTrue();
        }

        public Process Broker()
        {
            var info = new ProcessStartInfo(LocalProcessRunner.RunnerHostBinaryPath()) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add("broker"); info.ArgumentList.Add(Directory);
            var process = Process.Start(info)!; _workers.Add(process); return process;
        }

        public NativeLaunchInvocation Invocation()
        {
            var command = LocalProcessRunner.BuildDurableStartInfo(Request.Spec, Spool, bootstrapSession: true);
            return new NativeLaunchInvocation { Spec = Request.Spec, ReadOnlyPaths = Request.Spec.ReadOnlyPaths, Command = command.FileName, Args = command.ArgumentList.ToArray(), WorkingDirectory = command.WorkingDirectory, Environment = command.Environment.ToDictionary(pair => pair.Key, pair => pair.Value) };
        }

        public Process Observer(bool crash, bool missingHost = false)
        {
            var requestFile = Path.Combine(Root, "request.json");
            File.WriteAllText(requestFile, JsonSerializer.Serialize(Request, NativeLaunchProtocol.Json));
            var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            if (missingHost) info.Environment[LocalProcessRunner.RunnerHostPathEnvVar] = Path.Combine(Root, "missing-runner-host");
            foreach (var argument in new[] { "exec", "--runtimeconfig", Path.Combine(AppContext.BaseDirectory, "CodeSpace.UnitTests.runtimeconfig.json"), "--depsfile", Path.Combine(AppContext.BaseDirectory, "CodeSpace.UnitTests.deps.json"), typeof(RunnerTestWorker.RunnerTestWorker).Assembly.Location, requestFile, Barrier, crash ? "crash" : "return" }) info.ArgumentList.Add(argument);
            var process = Process.Start(info)!; _workers.Add(process); return process;
        }

        public async Task<SandboxHandle> ReadObserverAsync(Process process, int expectedExit = 0)
        {
            var output = await process.StandardOutput.ReadLineAsync(Token);
            await process.WaitForExitAsync(Token);
            process.ExitCode.ShouldBe(expectedExit, await process.StandardError.ReadToEndAsync(Token));
            return JsonSerializer.Deserialize<SandboxHandle>(output!, NativeLaunchProtocol.Json)!;
        }

        public async Task WaitAsync(Func<bool> condition)
        {
            while (!condition()) await Task.Delay(20, Token);
        }

        public async ValueTask DisposeAsync()
        {
            File.WriteAllText(Release, "release");
            try
            {
                var receipt = ReadReceipt();
                if (receipt.Execution is { } execution) NativeProcess.KillSession(execution);
                foreach (var identity in new[] { receipt.Guardian, receipt.Broker })
                    if (identity is not null && NativeProcess.IsAlive(identity)) { using var process = Process.GetProcessById(identity.ProcessId); process.Kill(); }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            foreach (var worker in _workers)
            {
                try { if (!worker.HasExited) worker.Kill(entireProcessTree: true); await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (InvalidOperationException) { }
                worker.Dispose();
            }
            await Task.Delay(100);
            System.IO.Directory.Delete(Spool, recursive: true); System.IO.Directory.Delete(Root, recursive: true);
            _deadline.Dispose();
        }
    }
}
