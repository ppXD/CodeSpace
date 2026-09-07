using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;

namespace CodeSpace.StorageTestWorker;

/// <summary>Runs the production storage driver in a killable process; stdin/stdout barriers avoid timing-based crash injection.</summary>
public static class StorageTestWorker
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 4) throw new ArgumentException("Expected root, object key, mode and payload byte.");
        var (root, key, mode, payloadByte) = (args[0], args[1], args[2], byte.Parse(args[3]));

        if (mode == "legacy-lock")
        {
            var directory = Path.Combine(root, ".codespace", "create-locks");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key))));
            await using var held = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await BarrierAsync("locked").ConfigureAwait(false);
            return 0;
        }

        var profile = new StorageProfileSnapshot
        {
            ProfileId = Guid.NewGuid(), ProfileRevision = 1, ProviderTypeKey = LocalRwxArtifactStorageDriverFactory.TypeKey,
            Configuration = JsonSerializer.SerializeToElement(new { rootPath = root }),
        };
        await using var driver = await new LocalRwxArtifactStorageDriverFactory().CreateAsync(new ArtifactStorageDriverCreateRequest(profile), CancellationToken.None).ConfigureAwait(false);
        var bytes = Enumerable.Repeat(payloadByte, 1024 * 1024).ToArray();
        await using var source = new CoordinatedStream(bytes, mode == "staged");
        var result = await driver.PutAsync(new ArtifactStoragePutRequest(key, source)
        {
            Condition = ArtifactStorageWriteCondition.CreateOnly, ContentLength = bytes.LongLength,
            ExpectedSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
        }, CancellationToken.None).ConfigureAwait(false);

        if (mode == "published" && result.IsSuccess) await BarrierAsync("published").ConfigureAwait(false);
        Console.WriteLine(result.IsSuccess ? "stored" : result.Error!.Code.ToString());
        return result.IsSuccess || result.Error?.Code == ArtifactStorageErrorCode.AlreadyExists ? 0 : 1;
    }

    private static async Task BarrierAsync(string name)
    {
        Console.WriteLine(name);
        if (await Console.In.ReadLineAsync().ConfigureAwait(false) != "continue") throw new IOException("Parent closed the process barrier.");
    }

    private sealed class CoordinatedStream(byte[] bytes, bool pauseAtEnd) : MemoryStream(bytes, writable: false)
    {
        private bool _started;
        private bool _ended;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_started)
            {
                _started = true;
                await BarrierAsync("ready").ConfigureAwait(false);
            }
            if (pauseAtEnd && Position == Length && !_ended)
            {
                _ended = true;
                await BarrierAsync("staged").ConfigureAwait(false);
            }
            return await base.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
    }
}
