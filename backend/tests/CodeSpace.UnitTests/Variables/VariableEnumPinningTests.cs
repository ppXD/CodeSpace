using CodeSpace.Messages.Enums;
using Shouldly;
using Xunit;

namespace CodeSpace.UnitTests.Variables;

/// <summary>
/// Pin the enum literal values + names for VariableScope and VariableValueType. These
/// strings are persisted by EF as VARCHAR (HasConversion&lt;string&gt;()), so changing them
/// breaks every existing row in the database. The integer values are also pinned because
/// some serialization paths default to numeric enum encoding — flipping the underlying
/// number would silently re-map data.
///
/// <para>If you genuinely need to add a new scope or value type, ADD a new enum entry
/// with a fresh numeric value and leave existing values untouched. Renames/reorders are
/// the bugs this test prevents.</para>
/// </summary>
[Trait("Category", "Unit")]
public class VariableEnumPinningTests
{
    [Theory]
    [InlineData(VariableScope.Team,     "Team",     0)]
    [InlineData(VariableScope.Workflow, "Workflow", 1)]
    public void VariableScope_NameAndIntValuePinned(VariableScope scope, string expectedName, int expectedInt)
    {
        scope.ToString().ShouldBe(expectedName);
        ((int)scope).ShouldBe(expectedInt);
    }

    [Theory]
    [InlineData(VariableValueType.String,  "String",  0)]
    [InlineData(VariableValueType.Number,  "Number",  1)]
    [InlineData(VariableValueType.Boolean, "Boolean", 2)]
    [InlineData(VariableValueType.Object,  "Object",  3)]
    [InlineData(VariableValueType.Array,   "Array",   4)]
    [InlineData(VariableValueType.Secret,  "Secret",  5)]
    public void VariableValueType_NameAndIntValuePinned(VariableValueType type, string expectedName, int expectedInt)
    {
        type.ToString().ShouldBe(expectedName);
        ((int)type).ShouldBe(expectedInt);
    }
}
