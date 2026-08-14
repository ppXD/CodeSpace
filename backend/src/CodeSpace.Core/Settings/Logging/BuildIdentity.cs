using System.Reflection;

namespace CodeSpace.Core.Settings.Logging;

/// <summary>
/// Which build this process is. Stamped on every log event and printed at startup, because the
/// alternative is having to infer it.
///
/// <para>Written after an incident that cost three rounds of diagnosis. A pod answered
/// <c>/api/auth/sign-in</c> with a Postgres 42703 for a column a migration had already renamed, which
/// only the code from BEFORE that migration would ask for — while the operator had built and pushed
/// the version after it. Tags had been reused, so nothing in the logs could say which build was
/// actually running, and the same staleness silently explained the second symptom: that image also
/// predated the Seq sink, so a correctly configured Seq stayed empty and looked like "no errors".</para>
///
/// <para>The value comes from <see cref="AssemblyInformationalVersionAttribute"/>, which the SDK
/// stamps with the commit sha appended as <c>1.2.3+abcdef…</c> when building from a git checkout. It
/// therefore identifies the SOURCE, not the tag someone chose to push it under — which is the only
/// form of the answer worth having.</para>
/// </summary>
public static class BuildIdentity
{
    /// <summary>Version plus commit sha when the SDK stamped one; the bare version otherwise.</summary>
    public static string Value { get; } = Resolve();

    private static string Resolve()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(BuildIdentity).Assembly;

        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational)) return informational;

        return assembly.GetName().Version?.ToString() ?? "unknown";
    }
}
