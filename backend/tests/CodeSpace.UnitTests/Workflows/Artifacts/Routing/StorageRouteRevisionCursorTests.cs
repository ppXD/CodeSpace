using CodeSpace.Core.Services.Workflows.Artifacts.Routing;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts.Routing;

public sealed class StorageRouteRevisionCursorTests
{
    [Fact]
    public void Cursor_is_opaque_stable_and_bound_to_one_route()
    {
        var routeId = Guid.NewGuid();
        var cursor = new StorageRouteRevisionCursor(routeId, 27, Guid.NewGuid());
        var encoded = cursor.Encode();

        encoded.ShouldNotContain(routeId.ToString("D"));
        StorageRouteRevisionCursor.Decode(encoded, routeId).ShouldBe(cursor);
        Should.Throw<InvalidOperationException>(() => StorageRouteRevisionCursor.Decode(encoded, Guid.NewGuid()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64")]
    [InlineData("e30")]
    public void Malformed_cursor_fails_closed(string value) =>
        Should.Throw<InvalidOperationException>(() => StorageRouteRevisionCursor.Decode(value, Guid.NewGuid()));
}
