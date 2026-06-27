namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// A pack import failed for an operator-actionable reason — a disallowed source host (egress guard) or a clone
/// failure. Thrown by the fetch path so the surface maps it to a clean 4xx/5xx with the message, rather than a
/// raw git / IO exception escaping.
/// </summary>
public sealed class PackImportException : Exception
{
    public PackImportException(string message) : base(message) { }

    public PackImportException(string message, Exception inner) : base(message, inner) { }
}
