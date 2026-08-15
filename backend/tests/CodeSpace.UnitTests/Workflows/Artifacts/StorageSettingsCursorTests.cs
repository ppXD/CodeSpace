using CodeSpace.Core.Services.Workflows.Artifacts;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts;

[Trait("Category", "Unit")]
public sealed class StorageSettingsCursorTests
{
    [Fact]
    public void Cursor_round_trips_the_exact_stable_name_and_uuid()
    {
        var expected = new StorageSettingsCursor("primary-store", Guid.Parse("11111111-2222-3333-4444-555555555555"));

        StorageSettingsCursor.Decode(expected.Encode()).ShouldBe(expected);
        StorageSettingsCursor.Decode(null).ShouldBeNull();
    }

    [Theory]
    [InlineData("not-base64")]
    [InlineData("")]
    [InlineData("cHJpbWFyeS1zdG9yZQ")]
    public void Malformed_non_null_cursor_fails_loud(string cursor)
    {
        if (cursor.Length == 0)
        {
            StorageSettingsCursor.Decode(cursor).ShouldBeNull();
            return;
        }

        Should.Throw<InvalidOperationException>(() => StorageSettingsCursor.Decode(cursor)).Message.ShouldContain("storage settings cursor");
    }
}
