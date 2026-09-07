using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using CodeSpace.Core.Persistence.Entities;

namespace CodeSpace.Core.Services.Agents.AgentRunLogging;

/// <summary>Versioned canonical manifest hash chain; deliberately never the SHA-256 of concatenated log content.</summary>
internal static class AgentRunLogManifestDigest
{
    public const string Kind = "segment-manifest-sha256-chain/v1";

    public static byte[] Begin(AgentRunLogVerification value)
    {
        using var hash = Domain("header");
        Add(hash, value.TeamId);
        Add(hash, value.AgentRunId);
        Add(hash, value.StreamId);
        Add(hash, value.WorkerFenceEpoch);
        Add(hash, value.CaptureSessionId);
        Add(hash, value.StreamRevision);
        Add(hash, value.SegmentCount);
        Add(hash, value.TotalBytes);
        Add(hash, value.SourceOffsetBytes);
        return hash.GetHashAndReset();
    }

    public static byte[] Append(byte[] previous, AgentRunLogManifestEntry entry)
    {
        using var hash = Domain("entry");
        hash.AppendData(previous);
        Add(hash, entry.Ordinal);
        Add(hash, entry.Offset);
        Add(hash, entry.Length);
        Add(hash, entry.ArtifactObjectId);
        hash.AppendData(entry.Digest);
        return hash.GetHashAndReset();
    }

    public static byte[] Seal(AgentRunLogVerification value)
    {
        using var hash = Domain("seal");
        hash.AppendData(value.Accumulator);
        Add(hash, value.SegmentCount);
        Add(hash, value.TotalBytes);
        return hash.GetHashAndReset();
    }

    private static IncrementalHash Domain(string kind)
    {
        var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.ASCII.GetBytes($"codespace.agent-run-log.manifest/{kind}/v1\0"));
        return hash;
    }

    private static void Add(IncrementalHash hash, Guid value) { Span<byte> bytes = stackalloc byte[16]; value.TryWriteBytes(bytes, bigEndian: true, out _); hash.AppendData(bytes); }
    private static void Add(IncrementalHash hash, long value) { Span<byte> bytes = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(bytes, value); hash.AppendData(bytes); }
}

internal sealed record AgentRunLogManifestEntry(long Ordinal, long Offset, long Length, Guid ArtifactObjectId, byte[] Digest);
