namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// A handle to a pack cloned into a transient worker directory — the caller walks <see cref="Directory"/>, then
/// DISPOSES this (a <c>using</c>) to delete the clone. Disposal is the happy-path cleanup that keeps transient
/// clones from accumulating on the worker's disk; a clone orphaned by a crash before disposal is the
/// <see cref="PackCloneFetcher"/> janitor's job (the crash-safety backstop). Best-effort delete: a failure to
/// remove the dir is swallowed (the janitor still reclaims it) rather than masking the caller's real result.
/// </summary>
public sealed class PackCheckout : IDisposable
{
    /// <summary>The transient directory the pack was cloned into. Valid until this handle is disposed.</summary>
    public string Directory { get; }

    internal PackCheckout(string directory) => Directory = directory;

    public void Dispose()
    {
        try
        {
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch
        {
            // Best-effort: the janitor sweep reclaims a clone we couldn't delete here, so a transient delete
            // failure must never surface over the caller's actual import result.
        }
    }
}
