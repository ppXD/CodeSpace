using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace CodeSpace.Core.Settings;

/// <summary>
/// Refuses to start on a configuration value that is still a deployment template.
///
/// <para>Release tooling renders placeholders into the shipped config — Octopus writes
/// <c>#{Some:Key}</c>, Azure DevOps writes <c>__SOME_KEY__</c>. When that step does not run, or the
/// variable is not defined in the release, the placeholder survives into the deployed file as an
/// ordinary non-empty string. Every check this codebase has asks whether a setting is SET, and a
/// placeholder is set — so it passes, and the value is used as though someone meant it.</para>
///
/// <para>What that looks like in practice: a pod took <c>#{Agents:RunSpoolDirectory}</c> as a path and
/// tried to create a directory of that name under its working directory, failing at the first agent
/// run with <c>Access to the path '/app/#{Agents:RunSpoolDirectory}' is denied</c> — an error about
/// permissions, three layers away from the release variable that was actually missing. A connection
/// string or a signing key would fail later still, and stranger.</para>
///
/// <para>So an unrendered placeholder is refused everywhere, not only in Production: it is never a
/// value anyone intended, and the environment it appears in does not change that. The failure names
/// the key and shows the value, which is safe — a template is not a secret, and it is precisely the
/// text the operator needs to find in their release definition.</para>
/// </summary>
public static class ConfigurationPlaceholderGuard
{
    /// <summary>
    /// Octopus <c>#{Section:Key}</c> and Azure DevOps <c>__NAME__</c> — and only when the body looks like
    /// a configuration key, meaning it carries a colon or is SHOUTING_CASE.
    ///
    /// <para>That narrowing is the whole design of this guard. Refusing to start is a heavy response, so
    /// a false positive costs far more than a false negative: missing <c>#{dbpassword}</c> brings back the
    /// original failure for one spelling, while firing on a value someone meant takes the service down.
    /// This product writes <c>#{number}</c> in its own prose — <c>"Fetch the diff of pull request
    /// #{number}"</c> — and prose reaches configuration through prompts. Octopus variables are named after
    /// the keys they replace, so requiring that shape costs nothing real.</para>
    ///
    /// <para>Deliberately not <c>{{...}}</c> or <c>${...}</c> at all: <c>{{project.default.X}}</c> is this
    /// product's own variable path, and <c>${...}</c> is ordinary shell text in a command setting.</para>
    /// </summary>
    private static readonly Regex Unrendered = new(@"#\{[A-Za-z0-9_.-]+:[A-Za-z0-9_.:-]+\}|#\{[A-Z][A-Z0-9_]*\}|__[A-Z][A-Z0-9_]*__", RegexOptions.Compiled);

    /// <summary>Every key whose value still carries a placeholder. Pure, so the rule is unit-pinned.</summary>
    public static IReadOnlyList<string> Violations(IConfiguration configuration)
    {
        var violations = new List<string>();

        foreach (var (key, value) in configuration.AsEnumerable())
        {
            if (string.IsNullOrEmpty(value)) continue;

            var match = Unrendered.Match(value);

            if (match.Success) violations.Add($"{key} = {value}   (unrendered {Describe(match.Value)})");
        }

        return violations;
    }

    /// <summary>Refuse to start rather than let a template be used as a value.</summary>
    public static void ThrowIfUnrendered(IConfiguration configuration)
    {
        var violations = Violations(configuration);

        if (violations.Count == 0) return;

        throw new InvalidOperationException(
            "Refusing to start: configuration still contains deployment placeholders that were never substituted. "
          + "The release step that renders them did not run, or these variables are not defined in the release:\n - "
          + string.Join("\n - ", violations));
    }

    private static string Describe(string placeholder) => placeholder.StartsWith('#') ? "Octopus #{...} placeholder" : "token-replacement __NAME__ placeholder";
}
