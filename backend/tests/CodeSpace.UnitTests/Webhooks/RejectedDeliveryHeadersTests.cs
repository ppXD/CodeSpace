using System.Text.Json;
using CodeSpace.Core.Services.Webhooks;
using Shouldly;

namespace CodeSpace.UnitTests.Webhooks;

/// <summary>
/// What a refused delivery's headers cost to keep. The audit row is written before anything is authenticated, so whoever
/// posts decides the header names and the safe-listed values: sixty forged posts with a two-thousand-character header
/// name stored 135 KB. The row keeps a bounded number of headers, each name and value bounded, and keeps the provider's
/// own headers first so the cap never drops the ones an operator matches against the provider's delivery log.
/// </summary>
[Trait("Category", "Unit")]
public class RejectedDeliveryHeadersTests
{
    [Fact]
    public void Secret_values_are_redacted_and_safe_values_kept()
    {
        var stored = Read(RejectedDeliveryHeaders.Serialize(new Dictionary<string, string> { ["X-Hub-Signature-256"] = "sha256=abc", ["X-GitHub-Event"] = "push", ["User-Agent"] = "GitHub-Hookshot/1" }));

        stored["X-Hub-Signature-256"].ShouldBe("[REDACTED]");
        stored["X-GitHub-Event"].ShouldBe("push");
        stored["User-Agent"].ShouldBe("GitHub-Hookshot/1");
    }

    [Fact]
    public void A_long_header_name_and_a_long_safe_value_are_truncated()
    {
        var name = new string('x', 2000);
        var stored = Read(RejectedDeliveryHeaders.Serialize(new Dictionary<string, string> { [name] = "v", ["User-Agent"] = new string('<', 5000) }));

        stored.Keys.ShouldAllBe(k => k.Length <= RejectedDeliveryHeaders.MaxNameLength + 1);
        stored["User-Agent"].Length.ShouldBe(RejectedDeliveryHeaders.MaxValueLength + 1);
        stored["User-Agent"].ShouldEndWith("…");
    }

    [Fact]
    public void Past_the_cap_the_rest_are_counted_not_stored_and_the_providers_own_headers_survive()
    {
        var headers = Enumerable.Range(0, 500).ToDictionary(i => $"X-Junk-{i}", _ => "v");
        headers["X-GitHub-Delivery"] = "d-1";
        headers["X-GitHub-Event"] = "pull_request";

        var json = RejectedDeliveryHeaders.Serialize(headers);
        var stored = Read(json);

        stored.Count.ShouldBe(RejectedDeliveryHeaders.MaxHeaders + 1, "the cap, plus one entry saying how many were left out");
        stored["X-GitHub-Delivery"].ShouldBe("d-1");
        stored["X-GitHub-Event"].ShouldBe("pull_request");
        stored[RejectedDeliveryHeaders.OmittedKey].ShouldBe($"{502 - RejectedDeliveryHeaders.MaxHeaders} more headers not kept");
        json.Length.ShouldBeLessThan(4096, "a forged delivery's audit row is bounded whatever was posted");
    }

    [Fact]
    public void The_caps_are_pinned()
    {
        RejectedDeliveryHeaders.MaxHeaders.ShouldBe(40);
        RejectedDeliveryHeaders.MaxNameLength.ShouldBe(64);
        RejectedDeliveryHeaders.MaxValueLength.ShouldBe(256);
    }

    private static Dictionary<string, string> Read(string json) => JsonSerializer.Deserialize<Dictionary<string, string>>(json)!;
}
