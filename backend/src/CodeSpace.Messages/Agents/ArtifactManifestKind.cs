namespace CodeSpace.Messages.Agents;

/// <summary>
/// DC-4: WHAT kind of thing a captured non-git deliverable is — the typed axis the untyped CAS store deliberately
/// doesn't carry. Derived from the declared path's extension at capture (best-effort, honest-default
/// <see cref="Other"/>); wire-stable (stored as text on <c>artifact_manifest.kind</c>) — renaming a member is a
/// data migration. Deliberately small: a kind earns its member when a consumer branches on it, never
/// speculatively.
/// </summary>
public enum ArtifactManifestKind
{
    /// <summary>Prose the deliverable IS — a report, a write-up, documentation (md/txt/rst/html/pdf/docx).</summary>
    Document,

    /// <summary>A rendered or renderable diagram (svg/mmd/drawio/puml/png).</summary>
    Diagram,

    /// <summary>Structured data (csv/tsv/json/jsonl/parquet/xlsx).</summary>
    Dataset,

    /// <summary>Anything else — the honest default, never a guess.</summary>
    Other,
}
