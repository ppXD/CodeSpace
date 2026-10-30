using System.Security.Cryptography;
using System.Text;
using CodeSpace.Core.Services.Variables;
using CodeSpace.Core.Services.Workflows.ExternalTriggers;
using Shouldly;
using Xunit;

namespace CodeSpace.UnitTests.Workflows;

[Trait("Category", "Unit")]
public sealed class ExternalTriggerKeySealerTests
{
    [Fact]
    public void A_minted_key_round_trips_through_its_own_digest_and_ciphertext()
    {
        var sealer = new ExternalTriggerKeySealer(new FixedEncryption());
        var minted = sealer.Mint();
        minted.Key.ShouldStartWith("ext_");
        minted.Key.Length.ShouldBe(47);
        minted.Digest.ShouldBe(ExternalTriggerKeySealer.Digest(minted.Key));
        sealer.Open(minted.Ciphertext).ShouldBe(minted.Key);
    }

    [Fact]
    public void The_digest_is_lowercase_hex_sha256_of_the_key()
    {
        var key = new ExternalTriggerKeySealer(new FixedEncryption()).Mint().Key;
        ExternalTriggerKeySealer.Digest(key).ShouldBe(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key))));
    }

    [Fact]
    public void Two_mints_produce_different_keys()
    {
        var sealer = new ExternalTriggerKeySealer(new FixedEncryption());
        sealer.Mint().Key.ShouldNotBe(sealer.Mint().Key);
    }

    private sealed class FixedEncryption : IVariableValueEncryption
    {
        public byte[] Encrypt(string plaintext) => Encoding.UTF8.GetBytes("sealed:" + plaintext);
        public string Decrypt(byte[] envelope) => Encoding.UTF8.GetString(envelope)["sealed:".Length..];
    }
}
