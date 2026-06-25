using CodeSpace.Core.Persistence.Db;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace CodeSpace.Core.Services.Credentials;

/// <summary>
/// Wires ASP.NET Data Protection — the key-ring that backs <see cref="DataProtectionPayloadEncryptor"/> (model-
/// credential + git-secret encryption) — to PERSIST in the shared Postgres (the <c>data_protection_keys</c> table)
/// under a STABLE application name, so EVERY pod (the public API + every worker replica) shares ONE key-ring.
///
/// <para>Without this, ASP.NET's default provider generates a per-process EPHEMERAL key-ring (or a node-local
/// filesystem one keyed by the content-root path): a credential encrypted by one pod then CANNOT be decrypted by
/// another replica, or by the same pod after a restart / rolling deploy. So a multi-replica k8s topology silently
/// breaks credential decryption the moment a second pod handles a row the first pod encrypted. The DB-backed,
/// app-name-pinned key-ring is the fix — it makes the API/worker split safe to scale.</para>
/// </summary>
public static class CodeSpaceDataProtection
{
    /// <summary>
    /// The application-name discriminator stamped on every protected payload. It MUST be IDENTICAL on every pod and
    /// STABLE across deploys — Data Protection refuses to decrypt a payload whose discriminator differs (the default
    /// discriminator is the content-root PATH, which differs per container — exactly why it must be pinned here).
    /// Changing this string ORPHANS every already-encrypted credential. <c>public const</c> + pinned by a test (Rule 8).
    /// </summary>
    public const string ApplicationName = "CodeSpace";

    /// <summary>Persist the Data Protection key-ring to the shared Postgres (via <see cref="CodeSpaceDbContext"/>) under the stable <see cref="ApplicationName"/>, so all pods share one key-ring. Call once from Startup.</summary>
    public static IServiceCollection AddCodeSpaceDataProtection(this IServiceCollection services)
    {
        services.AddDataProtection()
            .PersistKeysToDbContext<CodeSpaceDbContext>()
            .SetApplicationName(ApplicationName);

        return services;
    }
}
