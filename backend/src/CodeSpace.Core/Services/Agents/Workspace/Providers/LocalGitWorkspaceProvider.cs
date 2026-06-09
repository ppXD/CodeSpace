using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents.Workspace.Providers;

/// <summary>
/// v0 workspace provider: prepares the working copy as a <c>git clone</c> on the worker's own
/// filesystem, run THROUGH the "local" <see cref="ISandboxRunner"/> so it inherits the same process /
/// timeout / cancellation handling (workspace prep is just sandboxed git). Pairs with
/// <c>LocalProcessRunner</c> — both <see cref="Kind"/> "local". A future K8s provider clones into the
/// pod volume behind the same contract.
///
/// <para><b>Secret hygiene:</b> the access token is embedded in the clone URL for the clone command
/// only, then the origin remote is rewritten to the tokenless URL so the persisted <c>.git/config</c>
/// never retains it, and any token text is redacted from surfaced error output. (The transient argv
/// exposure is acceptable on a single-tenant local worker; the K8s runner injects via an in-pod
/// credential helper instead.)</para>
/// </summary>
public sealed class LocalGitWorkspaceProvider : IWorkspaceProvider, ISingletonDependency
{
    private const int CloneTimeoutSeconds = 300;
    private static readonly string WorkspacesRoot = Path.Combine(Path.GetTempPath(), "codespace-agent-workspaces");

    private readonly ISandboxRunnerRegistry _runners;
    private readonly ILogger<LocalGitWorkspaceProvider> _logger;

    public LocalGitWorkspaceProvider(ISandboxRunnerRegistry runners, ILogger<LocalGitWorkspaceProvider> logger)
    {
        _runners = runners;
        _logger = logger;
    }

    public string Kind => LocalProcessRunner.LocalKind;

    public async Task<IWorkspaceHandle> PrepareAsync(WorkspaceRequest request, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(WorkspacesRoot);

        var directory = Path.Combine(WorkspacesRoot, Guid.NewGuid().ToString("N"));
        var handle = new LocalWorkspaceHandle(directory, _logger);

        try
        {
            await CloneAsync(request, directory, cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(request.Token))
                await StripTokenFromRemoteAsync(request.RepositoryUrl, directory, cancellationToken).ConfigureAwait(false);

            return handle;
        }
        catch
        {
            await handle.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task CloneAsync(WorkspaceRequest request, string directory, CancellationToken cancellationToken)
    {
        var url = BuildAuthenticatedUrl(request.RepositoryUrl, request.TokenUsername, request.Token);

        var args = new List<string> { "clone" };

        if (request.Depth > 0) { args.Add("--depth"); args.Add(request.Depth.ToString()); }
        if (!string.IsNullOrWhiteSpace(request.Ref)) { args.Add("--branch"); args.Add(request.Ref); }

        args.Add(url);
        args.Add(directory);

        var result = await RunGitAsync(args, cancellationToken).ConfigureAwait(false);

        if (result.Status != SandboxStatus.Success)
            throw new WorkspaceException($"git clone failed (exit {result.ExitCode}): {Redact(Summarize(result.Stderr), request.Token)}");
    }

    /// <summary>Rewrite origin to the tokenless URL so the cloned <c>.git/config</c> never persists credentials. Best-effort — a failure is logged, not fatal (the clone already succeeded).</summary>
    private async Task StripTokenFromRemoteAsync(string cleanUrl, string directory, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(new[] { "-C", directory, "remote", "set-url", "origin", cleanUrl }, cancellationToken).ConfigureAwait(false);

        if (result.Status != SandboxStatus.Success)
            _logger.LogWarning("Could not strip the token from origin (exit {ExitCode}); the clone succeeded but .git/config may retain credentials", result.ExitCode);
    }

    private Task<SandboxResult> RunGitAsync(IReadOnlyList<string> args, CancellationToken cancellationToken) =>
        _runners.Resolve(Kind).RunAsync(new SandboxSpec { Command = "git", Args = args, TimeoutSeconds = CloneTimeoutSeconds }, cancellationToken);

    /// <summary>Build the HTTPS clone URL with embedded basic-auth credentials. No token → the URL unchanged. Pure + internal so it's unit-pinned.</summary>
    internal static string BuildAuthenticatedUrl(string repositoryUrl, string? tokenUsername, string? token)
    {
        if (string.IsNullOrEmpty(token)) return repositoryUrl;

        var uri = new Uri(repositoryUrl);
        var user = Uri.EscapeDataString(string.IsNullOrEmpty(tokenUsername) ? "x-access-token" : tokenUsername);
        var pass = Uri.EscapeDataString(token);

        return $"{uri.Scheme}://{user}:{pass}@{uri.Authority}{uri.PathAndQuery}";
    }

    private static string Summarize(string stderr) =>
        string.IsNullOrWhiteSpace(stderr) ? "(no stderr)" : stderr.Trim().Replace("\n", " ");

    /// <summary>Strip any echoed token from surfaced output so it never reaches a log / exception message.</summary>
    private static string Redact(string text, string? token) =>
        string.IsNullOrEmpty(token) ? text : text.Replace(token, "***");

    private sealed class LocalWorkspaceHandle : IWorkspaceHandle
    {
        private readonly ILogger _logger;

        public LocalWorkspaceHandle(string directory, ILogger logger)
        {
            Directory = directory;
            _logger = logger;
        }

        public string Directory { get; }

        public ValueTask DisposeAsync()
        {
            try
            {
                if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove agent workspace {Directory}", Directory);
            }

            return ValueTask.CompletedTask;
        }
    }
}
