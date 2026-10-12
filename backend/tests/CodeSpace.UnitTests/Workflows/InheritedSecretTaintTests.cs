using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Runtime;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// A child run's own template heads that carry a parent's secret: the trigger / input keys whose value embeds an
/// inherited value become secret paths, so <c>{{trigger.token}}</c> trips the same path-based guards
/// <c>{{wf.TOKEN}}</c> does in the parent. A key with no inherited value in it stays plain.
/// </summary>
[Trait("Category", "Unit")]
public sealed class InheritedSecretTaintTests
{
    private const string Secret = "CANARY-taint-a8e4c1d9";

    private static Dictionary<string, JsonElement> Bag(object value) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value))!;

    [Fact]
    public void Every_key_whose_value_embeds_an_inherited_secret_is_tainted_under_both_heads()
    {
        var trigger = Bag(new { token = Secret, header = new { auth = $"Bearer {Secret}" }, plain = "public" });
        var input = Bag(new { token = Secret, plain = "public" });

        var paths = InheritedSecretTaint.Paths(trigger, input, [Secret]);

        paths.ShouldBe(["trigger.token", "trigger.header", "input.token"], ignoreOrder: true);
    }

    [Fact]
    public void Nothing_inherited_taints_nothing()
    {
        var trigger = Bag(new { token = Secret });

        InheritedSecretTaint.Paths(trigger, trigger, []).ShouldBeEmpty();
    }
}
