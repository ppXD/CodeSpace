namespace CodeSpace.Core.Services.Workflows.Artifacts.Profiles;

/// <summary>
/// What a caller wants a storage profile for. The two are deliberately separate concepts because only one of them is
/// a lifecycle question: placing NEW bytes needs a live profile, while opening bytes that already exist needs only the
/// exact revision those bytes were stamped with. Every durable artifact location pins that revision, so a Disabled or
/// Retired profile keeps serving its own history. Blocking reads is a separate concept that does not exist yet -
/// profile lifecycle state must never become it by accident.
/// </summary>
public enum StorageProfileEligibility
{
    /// <summary>Place new bytes under the profile. Admitted only while the profile is Active; also the fail-closed default.</summary>
    Write,

    /// <summary>Open bytes already stamped with this exact profile revision. Admitted in every lifecycle state.</summary>
    Read,
}
