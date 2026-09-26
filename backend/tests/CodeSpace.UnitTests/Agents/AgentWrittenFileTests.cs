using System.Diagnostics;
using CodeSpace.Core.Services.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins how the worker opens a file the AGENT can write — its session transcript, in the config home it has write
/// access to. A named pipe planted there would block a plain open until a writer appears, which no cancellation token
/// interrupts, and the run's completion with it; the open must refuse it at once, and still read a real file whole.
/// </summary>
[Trait("Category", "Unit")]
public class AgentWrittenFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cs-agent-written-").FullName;

    [Fact]
    public async Task A_named_pipe_is_refused_at_once_instead_of_blocking_the_open()
    {
        if (OperatingSystem.IsWindows()) return;

        var fifo = Path.Combine(_dir, "rollout.jsonl");
        using (var mkfifo = Process.Start("mkfifo", fifo)!) await mkfifo.WaitForExitAsync();

        var open = Task.Run(() => { using var stream = AgentRunExecutor.OpenAgentWrittenFile(fifo); });

        (await Task.WhenAny(open, Task.Delay(TimeSpan.FromSeconds(5)))).ShouldBe(open, "the open must return at once — a blocking open of a FIFO waits for a writer that never comes");
        await Should.ThrowAsync<IOException>(open);
    }

    [Fact]
    public async Task A_regular_file_is_read_whole()
    {
        if (OperatingSystem.IsWindows()) return;

        var file = Path.Combine(_dir, "session.jsonl");
        await File.WriteAllTextAsync(file, "{\"type\":\"turn\"}\n");

        using var stream = AgentRunExecutor.OpenAgentWrittenFile(file);
        using var reader = new StreamReader(stream);

        (await reader.ReadToEndAsync()).ShouldBe("{\"type\":\"turn\"}\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup of a temp directory */ }
    }
}
