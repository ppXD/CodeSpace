using CodeSpace.Core.Services.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the pack-clone janitor's pure sweep logic — the crash-safety backstop that ages out clones orphaned by a
/// worker that died between clone and dispose (so transient clones never accumulate into an out-of-disk). Driven
/// against a controlled temp root + clock so it touches no real worker state.
/// </summary>
[Trait("Category", "Unit")]
public class PackCloneFetcherSweepTests
{
    [Fact]
    public void StaleThresholdEnvVar_name_is_pinned()
    {
        // Rule 8: operators tune the orphan-reclaim window via this env var.
        PackCloneFetcher.StaleThresholdEnvVar.ShouldBe("CODESPACE_PACK_CLONE_STALE_THRESHOLD");
    }

    [Theory]
    [InlineData(90, 60, true)]    // last write 90 min ago, threshold 60 min → stale
    [InlineData(30, 60, false)]   // 30 min ago, threshold 60 min → live (must NOT reclaim a possibly-running import)
    public void IsStale_compares_age_to_threshold(int ageMinutes, int thresholdMinutes, bool expected)
    {
        var now = new DateTime(2026, 6, 27, 12, 0, 0, DateTimeKind.Utc);
        var lastWrite = now.AddMinutes(-ageMinutes);

        PackCloneFetcher.IsStale(lastWrite, now, TimeSpan.FromMinutes(thresholdMinutes)).ShouldBe(expected);
    }

    [Fact]
    public void SweepStale_reclaims_only_clones_older_than_the_threshold()
    {
        var root = Path.Combine(Path.GetTempPath(), "cs-sweep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var now = DateTime.UtcNow;
            var stale = Path.Combine(root, "stale");
            var fresh = Path.Combine(root, "fresh");
            Directory.CreateDirectory(stale);
            Directory.CreateDirectory(fresh);
            Directory.SetLastWriteTimeUtc(stale, now.AddHours(-3));
            Directory.SetLastWriteTimeUtc(fresh, now.AddMinutes(-5));

            var reclaimed = PackCloneFetcher.SweepStale(root, TimeSpan.FromHours(1), now, default);

            reclaimed.ShouldBe(1);
            Directory.Exists(stale).ShouldBeFalse("the old orphan is reclaimed");
            Directory.Exists(fresh).ShouldBeTrue("a recent clone (a possibly-live import) is left alone");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void SweepStale_is_a_no_op_when_the_root_was_never_created()
    {
        var missing = Path.Combine(Path.GetTempPath(), "cs-sweep-missing-" + Guid.NewGuid().ToString("N"));

        PackCloneFetcher.SweepStale(missing, TimeSpan.FromHours(1), DateTime.UtcNow, default).ShouldBe(0);
    }
}
