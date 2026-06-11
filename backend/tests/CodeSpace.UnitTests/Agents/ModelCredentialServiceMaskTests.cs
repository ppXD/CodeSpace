using System.Linq;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Messages.Dtos.ModelCredentials;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the secret-masking pieces of the model-credential management surface: the last-4 hint, and the
/// structural guarantee that the read DTO has NO field that could carry a full key. The DB/team-scoping
/// branches are integration-pinned.
/// </summary>
[Trait("Category", "Unit")]
public class ModelCredentialServiceMaskTests
{
    [Theory]
    [InlineData("sk-ant-api03-1234abcd", "····abcd")]
    [InlineData("sk-or-9f3a", "····9f3a")]
    [InlineData("xy", "····xy")]          // shorter than 4 → whole value after the mask prefix
    [InlineData("", null)]
    [InlineData(null, null)]
    public void MaskKey_exposes_only_the_last_four(string? plaintext, string? expected) =>
        ModelCredentialService.MaskKey(plaintext).ShouldBe(expected);

    [Fact]
    public void MaskKey_never_returns_the_full_key()
    {
        const string key = "sk-ant-supersecret-tail";
        ModelCredentialService.MaskKey(key).ShouldNotContain("supersecret");
    }

    [Fact]
    public void Summary_dto_exposes_only_a_masked_hint_never_a_secret_field()
    {
        var props = typeof(ModelCredentialSummary).GetProperties().Select(p => p.Name).ToList();

        props.ShouldContain("KeyHint", "the read model surfaces only the masked tail");
        props.ShouldNotContain("ApiKey");
        props.ShouldNotContain("EncryptedApiKey");
        props.ShouldNotContain("Secret");
    }
}
