using CodeSpace.Core.Services.Providers.Source;
using Shouldly;

namespace CodeSpace.UnitTests.Providers.Source;

/// <summary>
/// Pure normalization behind the Languages bar — GitHub bytes → percent, GitLab percent pass-through,
/// ordering, rounding, zero-filtering, and the list cap. No SDK, every branch exercised directly.
/// </summary>
[Trait("Category", "Unit")]
public class LanguageBreakdownTests
{
    [Fact]
    public void FromBytes_computes_percentages_descending()
    {
        var result = LanguageBreakdown.FromBytes(new Dictionary<string, long> { ["C#"] = 900, ["Shell"] = 100 });

        result.Select(l => l.Name).ShouldBe(new[] { "C#", "Shell" });
        result[0].Percent.ShouldBe(90.0, 0.01);
        result[1].Percent.ShouldBe(10.0, 0.01);
    }

    [Fact]
    public void FromBytes_rounds_to_one_decimal()
    {
        var result = LanguageBreakdown.FromBytes(new Dictionary<string, long> { ["A"] = 1, ["B"] = 2 });

        result.Single(l => l.Name == "A").Percent.ShouldBe(33.3, 0.01);
        result.Single(l => l.Name == "B").Percent.ShouldBe(66.7, 0.01);
    }

    [Fact]
    public void FromBytes_ignores_zero_byte_and_empty()
    {
        LanguageBreakdown.FromBytes(new Dictionary<string, long>()).ShouldBeEmpty();
        LanguageBreakdown.FromBytes(new Dictionary<string, long> { ["X"] = 0 }).ShouldBeEmpty();
    }

    [Fact]
    public void FromPercents_orders_descending_and_passes_values_through()
    {
        var result = LanguageBreakdown.FromPercents(new Dictionary<string, double> { ["Shell"] = 1.1, ["C#"] = 97.4, ["PowerShell"] = 1.5 });

        result.Select(l => l.Name).ShouldBe(new[] { "C#", "PowerShell", "Shell" });
        result[0].Percent.ShouldBe(97.4, 0.01);
    }

    [Fact]
    public void Caps_the_list_at_MaxLanguages()
    {
        var many = Enumerable.Range(1, 30).ToDictionary(i => $"L{i}", i => (double)i);

        LanguageBreakdown.FromPercents(many).Count.ShouldBe(LanguageBreakdown.MaxLanguages);
    }
}
