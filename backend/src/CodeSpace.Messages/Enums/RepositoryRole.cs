namespace CodeSpace.Messages.Enums;

/// <summary>
/// A provider-neutral repository access ladder, ordered least → most privileged. Each provider maps its
/// native levels onto it (GitLab access_level Guest/Reporter/Developer/Maintainer/Owner; GitHub permission
/// pull/triage/push/maintain/admin) so the act-as-user pre-flight can compare an actor's role against the
/// role a capability needs with a single generic <c>actorRole &gt;= requiredRole</c> — no provider-specific
/// thresholds baked into the gate.
///
/// Numeric values are an explicit ordinal: only their ORDER is meaningful (used for the &gt;= comparison),
/// not the specific integers. Mirrors GitHub's own 5-rung model (which is the cleaner superset); GitLab's
/// 5 levels map one-to-one onto Read..Admin.
/// </summary>
public enum RepositoryRole
{
    /// <summary>No access — not a member, or can't see the repository at all.</summary>
    None = 0,

    /// <summary>Can read/clone and view (GitLab Guest, GitHub pull).</summary>
    Read = 1,

    /// <summary>Read plus issue/PR triage (GitLab Reporter, GitHub triage).</summary>
    Triage = 2,

    /// <summary>Can push and make attributable contributions — reviews, MR notes (GitLab Developer, GitHub push).</summary>
    Write = 3,

    /// <summary>Repository management short of ownership (GitLab Maintainer, GitHub maintain).</summary>
    Maintain = 4,

    /// <summary>Full administrative control (GitLab Owner, GitHub admin).</summary>
    Admin = 5
}
