using System.Security.Cryptography;
using System.Text;
using CodeSpace.Core.Services.Variables;
using CodeSpace.Core.Services.Workflows.Callbacks;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// A callback wait's token is a bearer credential for an anonymous route. A wait row keeps it three ways: the token as
/// minted (what a pod on the previous version compares verbatim, kept until a later release blanks it), its SHA-256 (what
/// a resume looks it up by, and the only lookup form left once the token is blanked) and its AES-GCM ciphertext under the
/// deployment's environment-held key (for the RunsControl-gated reveal). These pin the three forms a minted token takes,
/// which presented values may match the plaintext column, which rows a reveal can turn back into a URL, and that only the
/// deployment's key opens the ciphertext.
/// </summary>
[Trait("Category", "Unit")]
public sealed class WorkflowCallbackTokenSealerTests
{
    private const string MintedShape = "0123456789abcdef0123456789abcdef";

    private static readonly WorkflowCallbackTokenSealer Sealer = new(new AesGcmVariableEncryption(RandomNumberGenerator.GetBytes(32)));

    [Fact]
    public void A_minted_token_has_the_shape_the_callback_route_admits_with_its_digest_and_an_opaque_ciphertext()
    {
        var minted = Sealer.Mint();

        minted.Token.Length.ShouldBe(32, "WorkflowCallbacksController admits exactly {token:length(32)}");
        minted.Token.ShouldMatch("^[0-9a-f]{32}$", "the shape every earlier version minted, so a previous-version pod compares it verbatim");
        minted.Digest.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(minted.Token))));
        minted.Ciphertext.ShouldNotContain(minted.Token, customMessage: "the reveal copy is encrypted, never the token as text");
        Encoding.UTF8.GetString(Convert.FromBase64String(minted.Ciphertext)).ShouldNotContain(minted.Token, customMessage: "nor the token's bytes under an encoding");
    }

    [Fact]
    public void The_digest_is_lowercase_hex_sha256_so_it_equals_what_the_migration_writes_in_sql()
    {
        // encode(sha256(convert_to('abc', 'UTF8')), 'hex') — the FIPS 180-2 "abc" vector, in the case Postgres prints.
        WorkflowCallbackTokenSealer.Digest("abc").ShouldBe("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    }

    [Theory]
    [InlineData(MintedShape, true)]                                                                  // what every version mints and keeps in the token column
    [InlineData("0123456789ABCDEF0123456789ABCDEF", false)]                                          // never minted (Guid "N" is lowercase)
    [InlineData("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", false)]          // a stored digest presented as a token
    [InlineData("", false)]                                                                          // the blank a later release writes over the token
    [InlineData("CANARY-not-a-minted-token", false)]
    public void Only_a_minted_shape_token_is_looked_up_in_the_plaintext_column(string presented, bool matchesPlaintext)
    {
        WorkflowCallbackTokenSealer.PlaintextFormOf(presented).ShouldBe(matchesPlaintext ? presented : null, "a presented value is compared with the token column only when a version could have stored it there — so a digest, or a blanked column, is never a key");
    }

    [Fact]
    public void A_reveal_opens_the_ciphertext_even_once_the_plaintext_column_is_blanked()
    {
        var minted = Sealer.Mint();

        Sealer.Open(minted.Token, minted.Ciphertext).ShouldBe(minted.Token, "the reveal opens the exact token the URL needs");
        Sealer.Open(storedToken: "", minted.Ciphertext).ShouldBe(minted.Token, "the follow-up release blanks the token column; the ciphertext alone still reveals the URL");
    }

    [Fact]
    public void A_row_that_holds_only_its_plaintext_reveals_it()
    {
        // A row minted before 0248, or parked by a previous-version pod during the rollout or after a rollback: no ciphertext.
        Sealer.Open(MintedShape, ciphertext: null).ShouldBe(MintedShape);
    }

    [Theory]
    [InlineData("")]                                                                     // blanked by a later release, never sealed
    [InlineData("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]     // a digest is never handed out as a URL
    public void A_row_with_neither_a_ciphertext_nor_a_minted_token_cannot_be_revealed(string storedToken)
    {
        Sealer.Open(storedToken, ciphertext: null).ShouldBeNull();
    }

    [Fact]
    public void A_ciphertext_opens_only_with_the_deployment_key_it_was_sealed_under()
    {
        // The key lives in the deployment's environment, never in the database — a database or backup reader holds the
        // ciphertext and still cannot open it. Any other key fails closed rather than returning a wrong token.
        var minted = Sealer.Mint();
        var otherDeployment = new WorkflowCallbackTokenSealer(new AesGcmVariableEncryption(RandomNumberGenerator.GetBytes(32)));

        Should.Throw<CryptographicException>(() => otherDeployment.Open("", minted.Ciphertext));
    }
}
