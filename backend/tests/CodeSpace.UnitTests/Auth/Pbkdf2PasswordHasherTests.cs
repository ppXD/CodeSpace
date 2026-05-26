using CodeSpace.Core.Services.Auth;
using Shouldly;

namespace CodeSpace.UnitTests.Auth;

public class Pbkdf2PasswordHasherTests
{
    private readonly Pbkdf2PasswordHasher _hasher = new();

    [Fact]
    public void Hash_and_Verify_round_trip()
    {
        var hash = _hasher.Hash("hunter2");
        _hasher.Verify("hunter2", hash).ShouldBeTrue();
    }

    [Fact]
    public void Verify_returns_false_for_wrong_password()
    {
        var hash = _hasher.Hash("hunter2");
        _hasher.Verify("hunter3", hash).ShouldBeFalse();
    }

    [Fact]
    public void Hash_format_is_self_describing()
    {
        var hash = _hasher.Hash("password");

        var parts = hash.Split('$');
        parts.Length.ShouldBe(5);
        parts[0].ShouldBe("pbkdf2");
        parts[1].ShouldBe("sha256");
        int.Parse(parts[2]).ShouldBeGreaterThanOrEqualTo(100_000);
        // salt + digest are base64; non-empty
        parts[3].ShouldNotBeNullOrEmpty();
        parts[4].ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Two_hashes_of_same_password_differ_due_to_salt()
    {
        var a = _hasher.Hash("same");
        var b = _hasher.Hash("same");

        a.ShouldNotBe(b);
        _hasher.Verify("same", a).ShouldBeTrue();
        _hasher.Verify("same", b).ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-real-hash")]
    [InlineData("pbkdf2$sha256$100000$salt")] // too few segments
    [InlineData("pbkdf2$md5$100000$AA==$AA==")] // wrong algorithm
    [InlineData("pbkdf2$sha256$1$AA==$AA==")] // iterations too low
    [InlineData("pbkdf2$sha256$100000$@@$AA==")] // invalid base64
    public void Verify_returns_false_for_malformed_hash(string hash)
    {
        _hasher.Verify("anything", hash).ShouldBeFalse();
    }

    [Fact]
    public void Verify_returns_false_for_empty_password()
    {
        var hash = _hasher.Hash("real");
        _hasher.Verify(string.Empty, hash).ShouldBeFalse();
    }

    [Fact]
    public void Verify_matches_python_reference_implementation()
    {
        // Generated externally via:
        //   python3 -c "...pbkdf2_hmac('sha256', 'changeme123', bytes.fromhex('00112233...eeff'), 100000, 32)..."
        // Pin so a refactor of the hasher (e.g. switching iteration default) doesn't silently
        // invalidate the migration 0006 seed.
        const string referenceHash = "pbkdf2$sha256$100000$ABEiM0RVZneImaq7zN3u/w==$bwP/ed7w84HhCVbgaUB9HeNzng0mvKlsCEMboWQXiYw=";
        _hasher.Verify("changeme123", referenceHash).ShouldBeTrue();
    }
}
