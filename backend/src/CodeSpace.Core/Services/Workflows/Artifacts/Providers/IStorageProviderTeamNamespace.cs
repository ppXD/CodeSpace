namespace CodeSpace.Core.Services.Workflows.Artifacts.Providers;

/// <summary>
/// A provider whose namespace can be SUBDIVIDED, so that one operator-authored root can give every team a namespace of
/// its own. A sibling of <see cref="IStorageProviderModule"/> rather than part of it (Rule 7): subdivision is a property
/// of some destinations and not others, and a provider that cannot express it must be refused as a deployment default
/// rather than silently handed a root every team would share.
///
/// <para>Why it matters that each team gets its own: <c>ArtifactStore.ObjectKeyFor</c> builds
/// <c>workflow-artifacts/{aa}/{bb}/{sha256}</c> with no team segment, so two teams storing identical bytes land on one
/// key. Only a differing namespace makes those two keys two objects.</para>
/// </summary>
public interface IStorageProviderTeamNamespace
{
    /// <summary>
    /// The <see cref="IStorageProviderModule.ConfigSchema"/> property that carries the namespace. A deployment template
    /// must NOT set it — it names one team, and a template describes the whole deployment — so it is also the property
    /// template admission refuses.
    /// </summary>
    string TeamNamespaceProperty { get; }

    /// <summary>
    /// Joins an operator's root and one team's segment into a value this provider's own ConfigSchema accepts for
    /// <see cref="TeamNamespaceProperty"/>. The provider owns the join because the shape is its own: OSS wants a
    /// slash-terminated key prefix, a filesystem wants a path. The result is validated against the schema afterwards,
    /// so a join that produces something inadmissible fails loudly at admission rather than at the first write.
    /// </summary>
    string ComposeTeamNamespace(string namespaceRoot, string teamSegment);
}
