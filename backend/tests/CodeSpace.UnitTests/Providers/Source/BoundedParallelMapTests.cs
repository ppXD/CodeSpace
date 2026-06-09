using CodeSpace.Core.Services.Providers.Source;
using Shouldly;

namespace CodeSpace.UnitTests.Providers.Source;

/// <summary>
/// The best-effort bounded fan-out behind per-entry commit enrichment: it collects successes and silently
/// drops keys whose lookup throws or returns null, so one bad path never fails the whole file list.
/// </summary>
[Trait("Category", "Unit")]
public class BoundedParallelMapTests
{
    [Fact]
    public async Task Collects_every_successful_result()
    {
        var result = await BoundedParallelMap.RunAsync<string>(
            new[] { "a", "b", "c" }, 2, (key, _) => Task.FromResult<string?>(key.ToUpperInvariant()), CancellationToken.None);

        result.Count.ShouldBe(3);
        result["a"].ShouldBe("A");
        result["c"].ShouldBe("C");
    }

    [Fact]
    public async Task Omits_keys_whose_selector_throws()
    {
        var result = await BoundedParallelMap.RunAsync<string>(
            new[] { "ok", "boom" }, 4,
            (key, _) => key == "boom" ? throw new InvalidOperationException("nope") : Task.FromResult<string?>(key),
            CancellationToken.None);

        result.Keys.ShouldBe(new[] { "ok" });
    }

    [Fact]
    public async Task Omits_keys_whose_selector_returns_null()
    {
        var result = await BoundedParallelMap.RunAsync<string>(
            new[] { "keep", "drop" }, 4, (key, _) => Task.FromResult(key == "drop" ? null : key), CancellationToken.None);

        result.Keys.ShouldBe(new[] { "keep" });
    }

    [Fact]
    public async Task Empty_input_yields_empty_map()
    {
        var result = await BoundedParallelMap.RunAsync<string>(
            Array.Empty<string>(), 4, (key, _) => Task.FromResult<string?>(key), CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task Deduplicates_repeated_keys()
    {
        var calls = 0;
        var result = await BoundedParallelMap.RunAsync<string>(
            new[] { "x", "x", "x" }, 4, (key, _) => { Interlocked.Increment(ref calls); return Task.FromResult<string?>(key); }, CancellationToken.None);

        result.Count.ShouldBe(1);
        calls.ShouldBe(1);
    }
}
