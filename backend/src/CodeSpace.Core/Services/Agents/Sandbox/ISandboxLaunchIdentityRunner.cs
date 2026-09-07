using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Sandbox;

/// <summary>
/// Optional capability a durable runner MAY implement alongside <see cref="ISandboxDurableRunner"/> (Rule 7 / ISP — a
/// sibling interface, never a widening of the base contract): it binds a launch to a SERVER-MINTED identity and can
/// later re-discover that same launch from the identity alone.
///
/// <para><b>Why the base contract cannot express this.</b> <see cref="ISandboxDurableRunner.LaunchAsync"/> is handed
/// only a spool key, so the durable row that admitted the launch is unnameable from inside the runner and a replay
/// cannot be told from a fresh launch. Here the caller states WHICH durable attempt this physical process belongs to,
/// the runner binds that identity immutably beside the process, and a replay carrying the same identity discovers the
/// live execution instead of releasing a second one. A replay carrying a DIFFERENT identity is refused outright — that
/// refusal is the whole point, and it is why the identity has to be immutable rather than advisory.</para>
///
/// <para><b>What it does not promise.</b> Binding an identity is not external exactly-once: the window between the
/// bootstrap's start commitment and the process actually executing stays indeterminate, and a launch whose receipt is
/// unreadable remains indeterminate rather than being re-run. It is also not a cross-host or cross-boot capability —
/// an identity minted on another host or before this boot is refused, because the pid it names means nothing here.</para>
/// </summary>
public interface ISandboxLaunchIdentityRunner
{
    /// <summary>
    /// Launch (or discover) bound to <paramref name="request"/>'s explicit identity. Semantics are otherwise exactly
    /// <see cref="ISandboxDurableRunner.LaunchAsync"/>'s. A slot already bound to a different identity or invocation is
    /// refused instead of being reused.
    /// </summary>
    Task<SandboxHandle> LaunchAsync(SandboxLaunchRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Rebuild the handle for a launch this exact identity already admitted, WITHOUT being able to start one. Returns
    /// null when the slot holds no launch at all — the honest "nothing was ever admitted here", which is the only
    /// state from which re-launching is safe. Throws when a launch exists but cannot be adopted from here (bound to a
    /// different identity, minted on a foreign host/boot, or holding a receipt that is unreadable or was consumed
    /// without a confirmed execution) — an unknown outcome must never read as an absence.
    /// </summary>
    Task<SandboxHandle?> AdoptAsync(SandboxLaunchAdoption adoption, CancellationToken cancellationToken);
}
