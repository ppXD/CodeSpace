namespace CodeSpace.Core.Services.Workflows;

/// <summary>
/// Thrown when a from-node rerun (D7) can't reuse a KEPT (upstream) node because it did not produce a reusable
/// outcome in the original run — it's Running / Suspended / Pending, or Failed WITHOUT an error edge (so there is
/// no settled output to carry forward). Distinct from a side-effect block: the rerun graph is fine, but the
/// original simply never finished the part we'd reuse (e.g. rerun-from a node whose upstream never completed in a
/// Failed/Suspended original). Refused before any write; the message names the offending upstream node. Maps to 422.
/// </summary>
public sealed class RerunUpstreamNotReusableException : Exception
{
    public RerunUpstreamNotReusableException(string message) : base(message) { }
}
