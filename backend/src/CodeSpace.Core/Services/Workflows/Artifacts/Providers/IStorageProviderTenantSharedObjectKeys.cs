namespace CodeSpace.Core.Services.Workflows.Artifacts.Providers;

/// <summary>
/// A provider whose object keys are DEPLOYMENT-WIDE: one key names bytes that rows in more than one team may be
/// pointing at, so removing it is a cross-team act no team-scoped caller can authorize.
///
/// <para>A sibling marker rather than a widening of <see cref="IStorageProviderModule"/> (Rule 7): sharing keys is a
/// property of some destinations and not others, and a provider that does not share them must not have to say so.</para>
///
/// <para>Declaring it makes byte removal structurally impossible for the whole provider type:
/// <see cref="StorageProviderModuleCatalog"/> refuses a module that declares this marker AND
/// <see cref="StorageProviderCapabilities.Delete"/>, and every delete path in the plane requires that capability of
/// the driver before it asks for anything. A shared-key tier can therefore only ever close a record — never bytes.</para>
/// </summary>
public interface IStorageProviderTenantSharedObjectKeys;
