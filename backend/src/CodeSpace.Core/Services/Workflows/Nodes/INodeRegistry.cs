namespace CodeSpace.Core.Services.Workflows.Nodes;

/// <summary>
/// Lookup of all loaded <see cref="INodeRuntime"/> instances by their <c>TypeKey</c>.
/// The engine asks this for the runtime that matches a node definition's type_key.
/// Frontend asks for the list of available types to render the palette.
/// </summary>
public interface INodeRegistry
{
    /// <summary>Resolve by type key. Throws when the type isn't loaded — definition validator should have caught this at write time.</summary>
    INodeRuntime Resolve(string typeKey);

    /// <summary>True iff a node with this type key is loaded. Used by validator before dereferencing.</summary>
    bool Contains(string typeKey);

    /// <summary>Every loaded node. Drives /api/workflows/nodes for the editor palette.</summary>
    IReadOnlyList<INodeRuntime> All { get; }
}
