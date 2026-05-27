namespace CodeSpace.Core.Hardening;

/// <summary>
/// Parses a single env var into <see cref="EnforcementMode"/>. Shared by every hardening
/// validator so all three modes accept the same case-insensitive aliases — operators
/// who learn the vocabulary for one knob carry it to every other knob.
///
/// <para>Aliases mirror the conventions from CLAUDE.md Rule 11:
/// <list type="bullet">
///   <item><c>off</c> | <c>disabled</c> | <c>0</c> | <c>false</c> → <see cref="EnforcementMode.Off"/></item>
///   <item><c>warn</c> | <c>warning</c> → <see cref="EnforcementMode.Warn"/></item>
///   <item><c>strict</c> | <c>enforce</c> | <c>1</c> | <c>true</c> → <see cref="EnforcementMode.Strict"/></item>
/// </list>
/// Unrecognised values fall back to <paramref name="defaultMode"/> (also used when the
/// env var is unset or whitespace-only) — never throws so a typo can't crash startup.</para>
/// </summary>
public static class EnforcementModeReader
{
    public static EnforcementMode Read(string envVarName, EnforcementMode defaultMode = EnforcementMode.Warn)
    {
        var raw = Environment.GetEnvironmentVariable(envVarName);
        if (string.IsNullOrWhiteSpace(raw)) return defaultMode;

        return raw.Trim().ToLowerInvariant() switch
        {
            "off" or "disabled" or "0" or "false" => EnforcementMode.Off,
            "warn" or "warning"                   => EnforcementMode.Warn,
            "strict" or "enforce" or "1" or "true" => EnforcementMode.Strict,
            _                                      => defaultMode,
        };
    }
}
