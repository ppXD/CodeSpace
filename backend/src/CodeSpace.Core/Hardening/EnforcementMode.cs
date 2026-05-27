namespace CodeSpace.Core.Hardening;

/// <summary>
/// Three-mode enforcement primitive for hardening fixes that would, by becoming the
/// new default behavior, break existing deployments. The pattern (CLAUDE.md Rule 11):
/// every check carries an env var (per-validator), every env var resolves to one of
/// these three modes, every validator behaves the same way:
///
/// <list type="bullet">
///   <item><c>Off</c>     — silent allow. For dev / tests / explicit operator opt-out.</item>
///   <item><c>Warn</c>    — allow + structured log warning naming the value AND the env
///                          var to flip to <c>strict</c>. v0 default — preserves backward
///                          compat, makes the latent issue visible without breaking deploys.</item>
///   <item><c>Strict</c>  — reject (throw) with an actionable error message naming the
///                          remediation. Future major-release default once operators have
///                          had time to remediate.</item>
/// </list>
/// </summary>
public enum EnforcementMode
{
    Off,
    Warn,
    Strict,
}
