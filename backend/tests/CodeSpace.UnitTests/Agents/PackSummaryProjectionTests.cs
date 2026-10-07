using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: the pack read model every team member — Viewers included — receives never carries a URL's userinfo, even
/// for a row the clone-URL backfill has not sealed yet. The writer already stores a clean URL; this pins the read side
/// so the API is closed the moment the code deploys, not when the backfill reaches the row.
/// </summary>
[Trait("Category", "Unit")]
public class PackSummaryProjectionTests
{
    private const string Token = "ghp_FakeSummaryToken0123456789";

    [Theory]
    [InlineData("https://x-access-token:" + Token + "@github.com/acme/agents", "https://github.com/acme/agents")]   // a legacy, unsealed row
    [InlineData("https://" + Token + "@gitlab.com/acme/agents.git", "https://gitlab.com/acme/agents.git")]            // a token pasted as the user alone
    public void A_url_that_still_carries_a_credential_is_summarized_without_it(string stored, string expected)
    {
        var summary = PackService.ToSummary(PackWithUrl(stored), agentCount: 1, skillCount: 0);

        summary.Url.ShouldBe(expected);
        summary.Url.ShouldNotContain(Token, Case.Insensitive);
    }

    [Theory]
    [InlineData("https://github.com/acme/agents")]
    [InlineData("https://github.com/acme/agents@v2")]
    [InlineData(null)]
    public void A_clean_or_absent_url_is_summarized_byte_identical(string? stored)
    {
        PackService.ToSummary(PackWithUrl(stored), agentCount: 0, skillCount: 1).Url.ShouldBe(stored);
    }

    [Fact]
    public void The_summary_carries_no_sealed_source()
    {
        // The read model has no field the ciphertext could land in — a property added for it would show up here.
        typeof(CodeSpace.Messages.Dtos.Agents.PackSummary).GetProperties().Select(p => p.Name)
            .ShouldNotContain(name => name.Contains("Encrypted", StringComparison.OrdinalIgnoreCase) || name.Contains("Clone", StringComparison.OrdinalIgnoreCase) || name.Contains("Credential", StringComparison.OrdinalIgnoreCase));
    }

    private static Pack PackWithUrl(string? url) =>
        new() { Id = Guid.NewGuid(), TeamId = Guid.NewGuid(), Kind = url is null ? PackKind.Custom : PackKind.GitUrl, Name = "agents", Url = url, EncryptedCloneUrl = url is null ? null : "sealed" };
}
