using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Messages.Enums;
using Microsoft.AspNetCore.DataProtection;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: <see cref="PackCloneUrlProtector"/> is the one place a pack's clone URL is split into the credential-free
/// URL the pack stores and shows, and the sealed URL it clones from. It runs over the REAL
/// <see cref="DataProtectionPayloadEncryptor"/> (an ephemeral key ring, no mock), so a round trip proves the ciphertext
/// is what production would decrypt. The corpus covers every spelling a pasted token takes in a URL's userinfo, plus
/// the shapes that carry none — a public URL, an '@' in the path only, a local path — whose stored URL must stay
/// byte-identical to the paste.
/// </summary>
[Trait("Category", "Unit")]
public class PackCloneUrlProtectorTests
{
    /// <summary>A fake token; only its spellings are asserted on.</summary>
    private const string Token = "ghp_FakePackToken0123456789";

    /// <summary>A fake token whose URL spelling is percent-encoded (<c>/</c> and <c>@</c>), so its decoded spelling differs from its pasted one.</summary>
    private const string EncodedToken = "Fake%2Fpack%40Token";

    private static readonly PackCloneUrlProtector Protector = new(new DataProtectionPayloadEncryptor(new EphemeralDataProtectionProvider()));

    /// <summary>(pasted URL, the secret it carries or null, the URL the pack must store).</summary>
    public static TheoryData<string, string?, string> Corpus => new()
    {
        { "https://github.com/acme/agents", null, "https://github.com/acme/agents" },
        { $"https://x-access-token:{Token}@github.com/acme/agents", Token, "https://github.com/acme/agents" },
        { $"https://oauth2:{Token}@gitlab.com/acme/agents.git", Token, "https://gitlab.com/acme/agents.git" },
        { $"https://{Token}@github.com/acme/agents", Token, "https://github.com/acme/agents" },
        { $"https://{Token}:x-oauth-basic@github.com/acme/agents", Token, "https://github.com/acme/agents" },
        { $"https://x-access-token:{EncodedToken}@github.com/acme/agents", EncodedToken, "https://github.com/acme/agents" },
        { $"https://{Token}:@github.com/acme/agents", Token, "https://github.com/acme/agents" },
        { $"https://x-access-token:{Token}@git.example.com:8443/acme/agents.git", Token, "https://git.example.com:8443/acme/agents.git" },
        { $"https://x-access-token:{Token}@github.com./acme/agents", Token, "https://github.com./acme/agents" },
        { $"https://x-access-token:{Token}@github.com/acme/agents?ref=main", Token, "https://github.com/acme/agents?ref=main" },
        { $"http://x-access-token:{Token}@127.0.0.1:8080/remote.git", Token, "http://127.0.0.1:8080/remote.git" },
        { "https://github.com/acme/agents@v2", null, "https://github.com/acme/agents@v2" },
        { "/tmp/packs/a@b", null, "/tmp/packs/a@b" },
    };

    [Theory]
    [MemberData(nameof(Corpus))]
    public void Sealing_keeps_the_credential_out_of_the_stored_url_and_clones_the_exact_paste(string pasted, string? secret, string expectedUrl)
    {
        var source = Protector.Seal(pasted);

        source.Url.ShouldBe(expectedUrl, "the pack stores the pasted URL with its userinfo removed — and a URL that carries none byte-identical");

        if (secret is null)
        {
            source.EncryptedCloneUrl.ShouldBeNull("a URL that carries no credential has nothing to seal");
            return;
        }

        foreach (var spelling in Spellings(secret))
            source.Url.ShouldNotContain(spelling, Case.Insensitive, $"the stored URL must not hold the token in any spelling ('{spelling}')");

        source.EncryptedCloneUrl.ShouldNotBeNull("a URL that carries a credential is sealed");
        source.EncryptedCloneUrl.ShouldNotContain(secret, Case.Insensitive, "the sealed column holds ciphertext, not the URL");

        Protector.CloneUrlOf(PackFrom(source)).ShouldBe(pasted, "Sync clones the exact string the import cloned — nothing is recomposed");
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void CarriesCredential_agrees_with_whether_sealing_changes_the_url(string pasted, string? secret, string expectedUrl)
    {
        PackCloneUrlProtector.CarriesCredential(pasted).ShouldBe(secret is not null);
        PackCloneUrlProtector.CarriesCredential(pasted).ShouldBe(Protector.Seal(pasted).Url != pasted, "the backfill's filter and the writer's split are one rule");
        PackCloneUrlProtector.WithoutCredential(pasted).ShouldBe(expectedUrl);
    }

    [Fact]
    public void The_corpus_discriminates_both_ways()
    {
        // Without rows of both kinds, a CarriesCredential that answered a constant would pass the agreement theory.
        var secrets = Corpus.Select(row => (string?)row[1]).ToList();

        secrets.Count(s => s is null).ShouldBeGreaterThanOrEqualTo(2, "the corpus needs URLs that carry no credential");
        secrets.Count(s => s is not null).ShouldBeGreaterThanOrEqualTo(2, "the corpus needs URLs that carry one");
    }

    [Fact]
    public void An_unsealed_pack_clones_from_its_url()
    {
        // A legacy row the backfill has not reached yet keeps its token in the URL — and must keep syncing from it.
        var legacy = $"https://x-access-token:{Token}@github.com/acme/agents";

        Protector.CloneUrlOf(new Pack { Url = legacy, EncryptedCloneUrl = null }).ShouldBe(legacy);
    }

    [Fact]
    public void A_ciphertext_that_no_longer_decrypts_fails_without_naming_the_url_or_the_ciphertext()
    {
        var source = Protector.Seal($"https://x-access-token:{Token}@github.com/acme/agents");
        var tampered = source.EncryptedCloneUrl![..^4] + "AAAA";

        var ex = Should.Throw<PackImportException>(() => Protector.CloneUrlOf(new Pack { Url = source.Url, EncryptedCloneUrl = tampered }));

        ex.Message.ShouldNotContain(Token, Case.Insensitive);
        ex.Message.ShouldNotContain(tampered);
        ex.Message.ShouldNotContain(source.Url, Case.Insensitive, "the message is about the credential, not the source");
        ex.Message.ShouldContain("import it again", Case.Insensitive, "the operator is told how to recover");
    }

    [Fact]
    public void A_ciphertext_from_another_key_ring_fails_the_same_way()
    {
        var foreign = new PackCloneUrlProtector(new DataProtectionPayloadEncryptor(new EphemeralDataProtectionProvider())).Seal($"https://x-access-token:{Token}@github.com/acme/agents");

        Should.Throw<PackImportException>(() => Protector.CloneUrlOf(PackFrom(foreign))).Message.ShouldNotContain(Token, Case.Insensitive);
    }

    private static Pack PackFrom((string Url, string? EncryptedCloneUrl) source) =>
        new() { Id = Guid.NewGuid(), Kind = PackKind.GitUrl, Name = "pack", Url = source.Url, EncryptedCloneUrl = source.EncryptedCloneUrl };

    /// <summary>The secret as pasted, decoded, and re-encoded — any of them in the stored URL would leak it.</summary>
    private static IEnumerable<string> Spellings(string secret)
    {
        var decoded = Uri.UnescapeDataString(secret);

        return new[] { secret, decoded, Uri.EscapeDataString(decoded) }.Distinct(StringComparer.Ordinal);
    }
}
