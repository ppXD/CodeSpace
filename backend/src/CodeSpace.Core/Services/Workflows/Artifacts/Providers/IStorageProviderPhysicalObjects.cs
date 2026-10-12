using System.Text.Json;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Providers;

/// <summary>
/// A provider that can name the PHYSICAL object one of its keys reaches, because its namespace alone cannot: a
/// filesystem destination resolves every key through the operating system, which follows a link anywhere beneath the
/// configured root, so two roots that are different directories can still lead one key to one file.
///
/// <para>A sibling of <see cref="IStorageProviderModule"/> (Rule 7): for an object store the bucket and prefix the
/// namespace projection names, plus the key, already ARE the object, and such a provider must not have to say so.</para>
///
/// <para>The retention reaper asks this at sweep time, for every placement that shares a key with the one it is about
/// to delete, because object keys carry no team segment and the answer decides whether a delete removes another team's
/// bytes. So two keys that reach one object MUST answer identically, and an object that cannot be established throws:
/// the caller reads a throw as "possibly the same as anything", which only ever keeps a byte.</para>
/// </summary>
public interface IStorageProviderPhysicalObjects
{
    /// <summary>The object a delete of <paramref name="objectKey"/> would remove under this configuration, spelled one way, as it stands now.</summary>
    string PhysicalObject(JsonElement nonSecretConfiguration, string objectKey);
}
