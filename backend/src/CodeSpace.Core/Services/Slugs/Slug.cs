namespace CodeSpace.Core.Services.Slugs;

/// <summary>
/// The one canonical slug transliteration, shared by every entity that derives a URL/handle from a name
/// (Project, Workflow, Agent, Skill). Lowercase, keep ASCII <c>[a-z0-9_]</c>, collapse every other run to a
/// single hyphen, trim leading/trailing hyphens, cap at 64 (re-trimming a hyphen the cut may expose). Returns
/// the empty string when nothing usable survives — the caller decides the empty policy (throw, or fall back).
///
/// <para>Pure + deterministic. MUST stay byte-identical to the SQL mirror in the slug backfill migrations
/// (<c>0022_project</c> / <c>0042_agent_definition</c> / <c>0079_skill_definition</c> / <c>0099_workflow_slug</c>)
/// — the parity is pinned by golden-vector tests. A change here is a wire-contract change for
/// <c>project.{slug}.X</c> variable paths and every @-mention handle.</para>
/// </summary>
public static class Slug
{
    public const int MaxLength = 64;

    public static string Slugify(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        var sb = new System.Text.StringBuilder(name.Length);
        var lastWasHyphen = true;   // suppresses a leading hyphen
        foreach (var c in name)
        {
            if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_')
            {
                sb.Append(char.ToLowerInvariant(c));
                lastWasHyphen = false;
            }
            else if (!lastWasHyphen)
            {
                sb.Append('-');
                lastWasHyphen = true;
            }
        }

        var result = sb.ToString().TrimEnd('-');
        return result.Length <= MaxLength ? result : result[..MaxLength].TrimEnd('-');
    }
}
