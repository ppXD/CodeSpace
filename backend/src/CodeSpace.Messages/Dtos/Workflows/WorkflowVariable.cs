using System.Text.Json;

namespace CodeSpace.Messages.Dtos.Workflows;

/// <summary>
/// A named, typed variable declared at the workflow level. Three buckets share this shape
/// (with different storage / semantics):
///
///   - <b>Inputs</b>: parameters supplied per-run (manual run form or HTTP trigger body).
///                    Required ones must be present; defaults apply if absent.
///   - <b>Variables</b>: author-defined constants. Edited once in the editor, referenced by
///                       every node via <c>{{wf.&lt;name&gt;}}</c>. Frozen per workflow_version.
///   - <b>Outputs</b>: declares what the workflow EMITS to external consumers (callers of the
///                     HTTP API, parent workflows). Filled by the Terminal node's inputs.
///
/// Type is a JSON Schema — supports every JSON-representable shape (string, number, boolean,
/// array, object, file = object-with-conventional-shape, anything composite). The editor
/// renders an input form from this schema using the existing SchemaForm component, so
/// adding a new variable type is zero frontend work.
/// </summary>
public sealed record WorkflowVariable
{
    /// <summary>Stable identifier referenced via {{wf.X}} / {{input.X}} / supplied in output map.</summary>
    public required string Name { get; init; }

    /// <summary>Human display label for the editor. Falls back to Name.</summary>
    public string? Label { get; init; }

    /// <summary>Optional one-line hint shown under the input field. Markdown-free.</summary>
    public string? Description { get; init; }

    /// <summary>JSON Schema describing the value shape. Drives editor rendering + runtime validation.</summary>
    public required JsonElement Schema { get; init; }

    /// <summary>Default value when an Input isn't supplied for a run. Ignored for Outputs.</summary>
    public JsonElement? Default { get; init; }

    /// <summary>For Inputs: must be supplied at run time. Default false.</summary>
    public bool Required { get; init; }
}
