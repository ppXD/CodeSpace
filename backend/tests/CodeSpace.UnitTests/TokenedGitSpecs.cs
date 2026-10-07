using System.Text;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests;

/// <summary>
/// The call-site pins' view of a tokened git command, spelled out literally rather than through <c>TokenedGitCommand</c>
/// itself. The environment carries the whole shape — <c>GIT_CONFIG_COUNT</c> entries that empty the credential helper list
/// for the remote's scheme and authority and then name the helper that reads the credential, map the remote's URL to
/// itself so no operator rewrite rule moves it, and turn auto-gc and auto-maintenance off; the credential in the two
/// variables that helper reads; every trace2 target off — the runner is asked for the private directory the helper records
/// a refusal in, and no argv element names a credential helper. A spec carrying only part of that fails the test outright:
/// it is neither shape.
/// </summary>
internal static class TokenedGitSpecs
{
    /// <summary>
    /// The credential helper a tokened command names, literally: answers <c>get</c> from the two variables with the shell's
    /// builtin printf until <c>erase</c> records in the state directory that the remote refused the credential, then says so
    /// instead of answering; ignores <c>store</c>; answers nothing without the state directory.
    /// </summary>
    public const string Helper = """!f() { r="$CODESPACE_GIT_STATE/refused"; case "$1" in get) if test -e "$r"; then echo "the remote refused this credential; not offering it again" >&2; elif test -d "$CODESPACE_GIT_STATE"; then printf "username=%s\npassword=%s\n" "$CODESPACE_GIT_USERNAME" "$CODESPACE_GIT_PASSWORD"; fi;; erase) test -d "$CODESPACE_GIT_STATE" && : > "$r";; esac; }; f""";

    /// <summary>The variable the runner points at the command's private state directory.</summary>
    public const string StateVariable = "CODESPACE_GIT_STATE";

    private static readonly string[] CredentialVariables = { "CODESPACE_GIT_USERNAME", "CODESPACE_GIT_PASSWORD" };

    /// <summary>The environment a tokened command for the remote at <paramref name="url"/> (named without userinfo) carries besides the credential itself.</summary>
    public static IReadOnlyDictionary<string, string> ConfigFor(string url)
    {
        var scope = new Uri(url).GetLeftPart(UriPartial.Authority);

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GIT_CONFIG_COUNT"] = "6",
            ["GIT_CONFIG_KEY_0"] = $"credential.{scope}.helper",
            ["GIT_CONFIG_VALUE_0"] = "",
            ["GIT_CONFIG_KEY_1"] = $"credential.{scope}.helper",
            ["GIT_CONFIG_VALUE_1"] = Helper,
            ["GIT_CONFIG_KEY_2"] = $"url.{url}.insteadOf",
            ["GIT_CONFIG_VALUE_2"] = url,
            ["GIT_CONFIG_KEY_3"] = $"url.{url}.pushInsteadOf",
            ["GIT_CONFIG_VALUE_3"] = url,
            ["GIT_CONFIG_KEY_4"] = "gc.auto",
            ["GIT_CONFIG_VALUE_4"] = "0",
            ["GIT_CONFIG_KEY_5"] = "maintenance.auto",
            ["GIT_CONFIG_VALUE_5"] = "false",
            ["GIT_TRACE2"] = "0",
            ["GIT_TRACE2_EVENT"] = "0",
            ["GIT_TRACE2_PERF"] = "0",
        };
    }

    /// <summary>True when <paramref name="spec"/> runs as a tokened command for the remote at <paramref name="url"/>; false when it carries no part of one.</summary>
    public static bool RunsTokened(SandboxSpec spec, string url)
    {
        var expected = ConfigFor(url);
        var matching = expected.Count(kv => spec.Environment.TryGetValue(kv.Key, out var value) && value == kv.Value);
        var present = spec.Environment.Keys.Count(IsPartOfATokenedCommand);
        var stateDirectory = spec.ConfigHomeEnvVars.Count(v => v == StateVariable);
        var helperOnTheArgv = spec.Args.Any(a => a.StartsWith("credential.", StringComparison.Ordinal) && a.Contains(".helper", StringComparison.Ordinal));

        var tokened = matching == expected.Count && present == expected.Count + CredentialVariables.Length && stateDirectory == 1 && !helperOnTheArgv;
        var untokened = present == 0 && stateDirectory == 0 && !helperOnTheArgv;
        (tokened || untokened).ShouldBeTrue($"half a tokened command: {string.Join(' ', spec.Args)} | env {string.Join(' ', spec.Environment.Keys)} | state dirs {string.Join(' ', spec.ConfigHomeEnvVars)}");

        return tokened;
    }

    /// <summary>True when <paramref name="spec"/>'s environment carries exactly <paramref name="username"/> and <paramref name="password"/> as the credential.</summary>
    public static bool CarriesTheCredential(SandboxSpec spec, string username, string password) =>
        spec.Environment.TryGetValue("CODESPACE_GIT_USERNAME", out var u) && u == username && spec.Environment.TryGetValue("CODESPACE_GIT_PASSWORD", out var p) && p == password;

    /// <summary>True when an argv element carries <paramref name="secret"/> — raw, percent-encoded, or base64-encoded as a Basic credential carries it — or names an http(s) URL with userinfo.</summary>
    public static bool ArgvCarriesACredential(SandboxSpec spec, string secret) =>
        spec.Args.Any(a => a.Contains(secret, StringComparison.Ordinal) || a.Contains(Uri.EscapeDataString(secret), StringComparison.Ordinal) || Base64Forms(secret).Any(f => a.Contains(f, StringComparison.Ordinal)) || NamesUserInfo(a));

    /// <summary>
    /// What any base64 text that encodes <paramref name="secret"/> contains, whatever precedes it (a Basic credential's
    /// <c>user:</c>, of any length): one string per alignment of the secret against base64's three-byte groups, each the
    /// encoding of the groups that hold the secret's bytes alone.
    /// </summary>
    public static IReadOnlyList<string> Base64Forms(string secret) => Enumerable.Range(0, 3).Select(shift => Base64Form(secret, shift)).Where(f => f.Length >= 8).ToList();

    /// <summary>The base64 of <paramref name="secret"/> after <paramref name="shift"/> other bytes, cut to the groups that hold the secret's bytes alone.</summary>
    private static string Base64Form(string secret, int shift)
    {
        var bytes = Encoding.UTF8.GetBytes(secret);
        var encoded = Convert.ToBase64String(new byte[shift].Concat(bytes).ToArray());
        var start = shift == 0 ? 0 : 4;
        var end = (shift + bytes.Length) / 3 * 4;

        return end > start ? encoded[start..end] : "";
    }

    private static bool IsPartOfATokenedCommand(string name) =>
        name.StartsWith("GIT_CONFIG_", StringComparison.Ordinal) || name.StartsWith("GIT_TRACE2", StringComparison.Ordinal) || CredentialVariables.Contains(name);

    private static bool NamesUserInfo(string arg) =>
        Uri.TryCreate(arg, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && uri.UserInfo.Length > 0;
}
