using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Shouldly;

namespace CodeSpace.E2ETests.Infrastructure;

/// <summary>Real Git smart-HTTP remote for end-to-end fixtures. Seeding and readback are test infrastructure on the host; production clone/fetch/push still traverse the confined runner and network transport.</summary>
internal sealed class GitTestRemoteServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<Task> _requests = new();
    private Task? _accept;
    public string Root { get; }
    public string Url { get; private set; } = "";

    public GitTestRemoteServer(string root)
    {
        Root = root;
        // Reserve an ephemeral loopback port; retry binding only if another process won the close/bind race.
        for (var attempt = 0; ; attempt++)
        {
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Url = $"http://127.0.0.1:{port}/remote.git";
            _listener.Prefixes.Clear();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try { _listener.Start(); break; }
            catch (HttpListenerException) when (attempt < 4) { }
        }
        // Fixture disposal is synchronous; listener continuations must not capture the test's limited context.
        _accept = Task.Run(AcceptAsync);
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                var request = await _listener.GetContextAsync().WaitAsync(_stopping.Token);
                _requests.Add(ServeAsync(request));
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
        catch (HttpListenerException) when (_stopping.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_stopping.IsCancellationRequested) { }
    }

    private async Task ServeAsync(HttpListenerContext context)
    {
        try
        {
            using var input = new MemoryStream();
            await context.Request.InputStream.CopyToAsync(input, _stopping.Token);
            var info = StartInfo(Root, new[] { "http-backend" });
            info.RedirectStandardInput = true;
            info.Environment["GIT_PROJECT_ROOT"] = Root;
            info.Environment["GIT_HTTP_EXPORT_ALL"] = "1";
            info.Environment["PATH_INFO"] = context.Request.Url!.AbsolutePath;
            info.Environment["QUERY_STRING"] = context.Request.Url.Query.TrimStart('?');
            info.Environment["REQUEST_METHOD"] = context.Request.HttpMethod;
            info.Environment["CONTENT_TYPE"] = context.Request.ContentType ?? "";
            info.Environment["CONTENT_LENGTH"] = input.Length.ToString();
            info.Environment["REMOTE_USER"] = "fixture";
            using var process = Process.Start(info).ShouldNotBeNull();
            using var output = new MemoryStream();
            var read = process.StandardOutput.BaseStream.CopyToAsync(output, _stopping.Token);
            var error = process.StandardError.ReadToEndAsync(_stopping.Token);
            input.Position = 0;
            await input.CopyToAsync(process.StandardInput.BaseStream, _stopping.Token);
            process.StandardInput.Close();
            try { await Task.WhenAll(read, error, process.WaitForExitAsync(_stopping.Token)).WaitAsync(TimeSpan.FromSeconds(30)); }
            catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
            process.ExitCode.ShouldBe(0, await error);
            var bytes = output.ToArray();
            var boundary = FindHeadersEnd(bytes);
            boundary.ShouldBeGreaterThan(0, "git http-backend must emit CGI headers");
            foreach (var header in Encoding.ASCII.GetString(bytes, 0, boundary).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = header.TrimEnd('\r').Split(':', 2);
                if (pair.Length != 2) continue;
                if (pair[0].Equals("Status", StringComparison.OrdinalIgnoreCase)) context.Response.StatusCode = int.Parse(pair[1].Trim().Split(' ')[0]);
                else if (pair[0].Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) context.Response.ContentType = pair[1].Trim();
                else context.Response.Headers[pair[0]] = pair[1].Trim();
            }
            context.Response.ContentLength64 = bytes.Length - boundary;
            await context.Response.OutputStream.WriteAsync(bytes.AsMemory(boundary), _stopping.Token);
        }
        finally { context.Response.Close(); }
    }

    private static int FindHeadersEnd(byte[] bytes)
    {
        for (var index = 1; index < bytes.Length; index++)
        {
            if (bytes[index - 1] == '\n' && bytes[index] == '\n') return index + 1;
            if (index >= 3 && bytes[index - 3] == '\r' && bytes[index - 2] == '\n' && bytes[index - 1] == '\r' && bytes[index] == '\n') return index + 1;
        }
        return -1;
    }

    public static async Task<string> RunFixtureGitAsync(string cwd, IReadOnlyList<string> args)
    {
        using var process = Process.Start(StartInfo(cwd, args)).ShouldNotBeNull();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await Task.WhenAll(output, error, process.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(30)); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        process.ExitCode.ShouldBe(0, await error);
        return await output;
    }

    private static ProcessStartInfo StartInfo(string cwd, IReadOnlyList<string> args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        info.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        info.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(cwd, "absent-global-config");
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        return info;
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _listener.Close();
        try
        {
            _accept?.GetAwaiter().GetResult();
            Task.WhenAll(_requests).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
        finally { _stopping.Dispose(); }
    }
}
