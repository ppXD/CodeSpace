using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Settings;
using CodeSpace.Core.Settings.Logging;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Health;

/// <summary>
/// Which build this process is and which role the deployment gave it — what <c>/health</c> and the boot line print so an API
/// image and a worker image can be compared from outside. Both facts are the ones the process already acts on: the build is
/// <see cref="BuildIdentity"/> (stamped on every log line) and the role is <see cref="HangfireHostingSetting"/>, resolved by the
/// same rule that decides whether this process starts job servers, so the report cannot disagree with the behaviour.
///
/// <para>The role is <c>Api</c> or <c>Worker</c> because that is all the deployment chooses between. Every process serves HTTP,
/// so <c>Worker</c> is also what a single all-in-one process is: it serves the API and processes the queue.</para>
/// </summary>
public sealed class HostPosture : ISingletonDependency
{
    public HostPosture(HangfireHostingSetting role) : this(role.Value, BuildIdentity.Value) { }

    internal HostPosture(HangfireHosting role, string build)
    {
        Role = role;
        Build = build;
    }

    /// <summary>Version plus commit sha, as stamped on every log line.</summary>
    public string Build { get; }

    public HangfireHosting Role { get; }

    /// <summary>The role's name as <c>/health</c> and the boot line print it — a contract, pinned by test.</summary>
    public string RoleName => Role.ToString();

    /// <summary>Whether this process runs Hangfire job servers: only the Worker role does.</summary>
    public bool ProcessesJobs => Role == HangfireHosting.Worker;

    /// <summary>The role in a phrase, for the boot line.</summary>
    public string RoleMeaning => ProcessesJobs
        ? "processes jobs and owns the recurring schedule; also serves HTTP"
        : "serves HTTP and enqueues jobs; processes no jobs";
}
