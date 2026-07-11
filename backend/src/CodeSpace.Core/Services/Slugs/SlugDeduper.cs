namespace CodeSpace.Core.Services.Slugs;

/// <summary>
/// The one auto-suffix dedup loop, shared by every entity whose slug is display-only and may collide
/// (Workflow, Agent, Skill — NOT Project, which refuses on collision). Returns <paramref name="baseSlug"/>,
/// or the first <c>-N</c> variant (N = 2, 3, …) free of both <paramref name="taken"/> and
/// <paramref name="reserved"/>, trimming the base so the numeric suffix always fits the 64-char slug column.
///
/// <para>Each caller owns only its <paramref name="taken"/> query (the table + scope filter); the correctness-
/// sensitive trim/cap math lives here once. Pinned by <c>SlugDeduperTests</c>.</para>
/// </summary>
public static class SlugDeduper
{
    /// <summary>
    /// The prefix to prefetch candidate slugs on: <paramref name="baseSlug"/>, or its first 50 chars — a prefix
    /// of <paramref name="baseSlug"/> AND of every trimmed <c>-N</c> variant (the shortest trim is ~58 chars, and
    /// <see cref="Slug.Slugify"/> emits no consecutive hyphens), so one <c>StartsWith</c> probe covers them all.
    /// The exact <c>taken.Contains</c> checks below stay precise, so an over-matched unrelated slug is ignored.
    /// </summary>
    public static string ProbePrefix(string baseSlug) => baseSlug.Length <= 50 ? baseSlug : baseSlug[..50];

    public static string DeriveAvailable(string baseSlug, IReadOnlySet<string> taken, IReadOnlySet<string>? reserved = null)
    {
        if (!taken.Contains(baseSlug) && (reserved is null || !reserved.Contains(baseSlug))) return baseSlug;

        for (var n = 2; n < 10000; n++)
        {
            var suffix = $"-{n}";
            var trimmed = baseSlug.Length + suffix.Length <= Slug.MaxLength ? baseSlug : baseSlug[..(Slug.MaxLength - suffix.Length)].TrimEnd('-');
            var candidate = trimmed + suffix;

            if (!taken.Contains(candidate) && (reserved is null || !reserved.Contains(candidate))) return candidate;
        }

        throw new InvalidOperationException($"Could not derive a free slug from base '{baseSlug}' — too many existing variants.");
    }
}
