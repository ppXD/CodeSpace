using System.Text.Json;

namespace CodeSpace.Core.Services.Workflows.Runtime;

/// <summary>
/// The template paths of a run's own scope that carry a secret it inherited — the trigger / input keys of a
/// <c>flow.subworkflow</c> child whose value holds a parent's secret. Added to <see cref="NodeRunScope.SecretPaths"/>,
/// they make the path-based guards (the node.started template redactor, the Terminal output mapping check, a map's
/// sensitivity class) treat <c>{{trigger.token}}</c> exactly like the <c>{{wf.TOKEN}}</c> it came from.
/// </summary>
public static class InheritedSecretTaint
{
    /// <summary>Every <c>trigger.&lt;key&gt;</c> and <c>input.&lt;key&gt;</c> whose value embeds one of <paramref name="secrets"/>. Empty when nothing was inherited.</summary>
    public static IReadOnlyList<string> Paths(IReadOnlyDictionary<string, JsonElement> trigger, IReadOnlyDictionary<string, JsonElement> input, IReadOnlyList<string> secrets)
    {
        if (secrets.Count == 0) return [];

        var redactor = new PersistenceSecretRedactor(secrets);

        return TaintedKeys("trigger", trigger, redactor).Concat(TaintedKeys("input", input, redactor)).ToList();
    }

    private static IEnumerable<string> TaintedKeys(string head, IReadOnlyDictionary<string, JsonElement> bag, PersistenceSecretRedactor redactor) =>
        bag.Where(entry => redactor.FoundIn(entry.Value).Count > 0).Select(entry => $"{head}.{entry.Key}");
}
