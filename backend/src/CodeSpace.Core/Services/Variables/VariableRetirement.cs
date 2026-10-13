using CodeSpace.Core.Persistence.Entities;

namespace CodeSpace.Core.Services.Variables;

/// <summary>
/// What deleting a variable leaves: the row, for the audit trail, and nothing a secret could be recovered from. Every
/// read of a variable filters on <c>deleted_date IS NULL</c> and nothing restores a deleted one, so a deleted Secret's
/// ciphertext could only ever be opened by someone holding a copy of the database and the master key. A plain value is
/// no secret and stays as the record of what the variable held. One rule for every way a variable is deleted — on its
/// own, or with the project it belongs to.
/// </summary>
public static class VariableRetirement
{
    public static void Retire(Variable row, DateTimeOffset now, Guid actorUserId)
    {
        row.DeletedDate = now;
        row.LastModifiedDate = now;
        row.LastModifiedBy = actorUserId;
        row.ValueEncrypted = null;
    }
}
