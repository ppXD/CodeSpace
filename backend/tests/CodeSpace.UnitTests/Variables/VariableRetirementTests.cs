using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Variables;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Variables;

/// <summary>
/// Deleting a variable keeps its row for the audit trail and drops the only thing in it that is still a secret: a
/// Secret's ciphertext. Nothing reads a deleted row's value — every read filters on <c>deleted_date IS NULL</c> and
/// nothing restores one — so the envelope could only ever be opened by someone holding the database and the key. A
/// plain value is not a secret and stays, as the history of what the variable was.
/// </summary>
[Trait("Category", "Unit")]
public sealed class VariableRetirementTests
{
    private static readonly DateTimeOffset Now = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(VariableValueType.Secret)]
    [InlineData(VariableValueType.String)]
    public void Retiring_stamps_the_deletion_and_drops_a_secrets_ciphertext_only(VariableValueType valueType)
    {
        var actor = Guid.NewGuid();
        var row = new Variable
        {
            Id = Guid.NewGuid(), Name = "TOKEN", ValueType = valueType,
            ValueEncrypted = valueType == VariableValueType.Secret ? new byte[] { 1, 2, 3 } : null,
            ValuePlain = valueType == VariableValueType.Secret ? null : "\"kept\"",
        };

        VariableRetirement.Retire(row, Now, actor);

        row.DeletedDate.ShouldBe(Now);
        row.LastModifiedDate.ShouldBe(Now);
        row.LastModifiedBy.ShouldBe(actor);
        row.ValueEncrypted.ShouldBeNull("a deleted variable holds no ciphertext anyone could open later");
        row.ValuePlain.ShouldBe(valueType == VariableValueType.Secret ? null : "\"kept\"", "a plain value is no secret and stays as the record of what the variable held");
    }
}
