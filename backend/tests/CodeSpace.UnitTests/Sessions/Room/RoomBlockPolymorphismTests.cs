using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeSpace.Messages.Dtos.Sessions.Room;
using Shouldly;

namespace CodeSpace.UnitTests.Sessions.Room;

/// <summary>
/// Every concrete <see cref="RoomBlock"/> must survive serialization through the API's JSON options.
///
/// <para>System.Text.Json defaults to <c>JsonUnknownDerivedTypeHandling.FailSerialization</c>: a block whose runtime
/// type is absent from the <see cref="JsonDerivedTypeAttribute"/> list throws <see cref="NotSupportedException"/>
/// mid-write. The room endpoint returns the whole <see cref="RoomView"/> as ONE document, so that throw 500s the
/// entire room rather than dropping one card. Discovering the subclasses by REFLECTION is the point: a new block
/// added without a discriminator fails here, at the unit tier, instead of in production.</para>
/// </summary>
public sealed class RoomBlockPolymorphismTests
{
    /// <summary>Mirrors CodeSpace.Api's <c>AddJsonOptions</c> — MVC's own options are web defaults plus the string enum converter.</summary>
    private static readonly JsonSerializerOptions ApiJson = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static TheoryData<Type> EveryConcreteBlock()
    {
        var data = new TheoryData<Type>();

        foreach (var type in typeof(RoomBlock).Assembly.GetTypes().Where(IsConcreteBlock).OrderBy(t => t.Name))
            data.Add(type);

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryConcreteBlock))]
    public void Every_room_block_round_trips_through_the_api_json_options(Type blockType)
    {
        // Uninitialized rather than constructed: the subclasses are discovered by reflection and their required
        // members differ, so there is no generic way to new one up. Serialization only needs the runtime TYPE.
        var block = (RoomBlock)RuntimeHelpers.GetUninitializedObject(blockType);

        var wire = JsonSerializer.Serialize(block, ApiJson);

        var discriminator = JsonDocument.Parse(wire).RootElement.GetProperty("type").GetString();
        discriminator.ShouldNotBeNullOrWhiteSpace($"{blockType.Name} serialized without a type discriminator");
        JsonSerializer.Deserialize<RoomBlock>(wire, ApiJson).ShouldBeOfType(blockType, $"\"{discriminator}\" did not read back as {blockType.Name}");
    }

    private static bool IsConcreteBlock(Type type) => type.IsClass && !type.IsAbstract && typeof(RoomBlock).IsAssignableFrom(type);

    [Fact]
    public void The_census_actually_found_the_blocks()
    {
        // A reflection sweep that silently matches nothing is a green test that proves nothing.
        EveryConcreteBlock().Count.ShouldBeGreaterThan(5);
    }
}
