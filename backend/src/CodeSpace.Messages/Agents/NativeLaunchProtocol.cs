using System.Security.Cryptography;
using System.Text.Json;

namespace CodeSpace.Messages.Agents;

/// <summary>Host-private protocol shared by the local runner and its bundled bootstrap. None of these records is a model input or an authority grant.</summary>
public static class NativeLaunchProtocol
{
    public const int Version = 1;
    public const int MaximumFrameBytes = 16 * 1024 * 1024;

    /// <summary>The share of <see cref="MaximumFrameBytes"/> an agent's standard input may take, checked before a spool exists so a goal no attempt can carry is refused before any work. Half the frame, leaving the rest room for everything else a launch carries — a continued session's restored transcript included. Measured as encoded for the pipe (<see cref="EncodedBytes"/>).</summary>
    public const int LargeCarrierBudgetBytes = MaximumFrameBytes / 2;

    /// <summary>What the frame keeps free beyond a spec for what wraps it — the child's command, environment and paths, a few kilobytes in practice — so a spec that fits by <see cref="FitsTheFrame"/> fits the frame it is sent in.</summary>
    public const int InvocationAllowanceBytes = 1024 * 1024;

    /// <summary>Whether a built spec, once encoded for the pipe, leaves the frame room for what wraps it. Measures everything the spec carries at once — goal, restored transcript, the persona's files — so a continuation is judged on what it actually sends, not on any one part of it.</summary>
    public static bool FitsTheFrame(SandboxSpec spec) => JsonSerializer.SerializeToUtf8Bytes(spec, Json).LongLength <= MaximumFrameBytes - InvocationAllowanceBytes;

    /// <summary>The bytes <paramref name="value"/> occupies once encoded for the frame. The web JSON defaults escape every non-ASCII character, so CJK text roughly doubles and emoji triple.</summary>
    public static int EncodedBytes(string value) => JsonSerializer.SerializeToUtf8Bytes(value, Json).Length;
    public const string DirectoryName = "launch-v1";
    public const string RequestFile = "request.json";
    public const string CommitmentFile = "commitment.json";
    public const string ReceiptFile = "receipt.json";
    public const string ExecutionFile = "execution.json";
    public const string GuardianFile = "guardian.json";
    public const string StopFile = "stop.json";
    /// <summary>Where the bootstrap's OWN diagnostics land. Its stderr is otherwise a pipe the launching app never reads and closes as soon as the handle is built — so a bootstrap that refuses or dies after that point says why into a closed pipe, and every reader of the failed launch sees a bare dead pid. Reasons only: never argv, environment or any other invocation input.</summary>
    public const string DiagnosticsFile = "bootstrap.err";
    public const string BinaryName = "codespace-runner-host";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static SandboxSpec Freeze(SandboxSpec spec)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spec.Command);
        if (spec.Command.Contains('\0')) throw new ArgumentException("The executable contains NUL.", nameof(spec));
        return spec with
        {
            Args = Copy(spec.Args), ReadOnlyPaths = Copy(spec.ReadOnlyPaths), ConfigHomeEnvVars = Copy(spec.ConfigHomeEnvVars),
            Environment = new SortedDictionary<string, string>(spec.Environment.ToDictionary(pair => pair.Key, pair => pair.Value), StringComparer.Ordinal),
            EgressAllowlist = spec.EgressAllowlist is null ? null : Copy(spec.EgressAllowlist), McpDeclarationArgs = Copy(spec.McpDeclarationArgs),
            ConfigHomeFiles = spec.ConfigHomeFiles.Select(file => file with { }).ToArray(), Mcp = spec.Mcp is null ? null : spec.Mcp with { },
            CaptureBudget = spec.CaptureBudget is null ? null : spec.CaptureBudget with { },
        };
    }

    public static string SpecHash(SandboxSpec frozen)
    {
        // These two server-only fields are deliberately JsonIgnore on task transport. They still alter execution
        // and MUST participate in binding. No canonical plaintext is persisted: it can contain credentials.
        var element = JsonSerializer.SerializeToElement(new { Version, Spec = frozen, frozen.ReadOnlyPaths, frozen.CaptureBudget }, Json);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) WriteCanonical(writer, element);
        return Convert.ToHexStringLower(SHA256.HashData(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length))));
    }

    private static string[] Copy(IReadOnlyList<string> values)
    {
        var result = values.ToArray();
        if (result.Any(value => value is null || value.Contains('\0'))) throw new ArgumentException("An invocation string is null or contains NUL.");
        return result;
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal)) { writer.WritePropertyName(property.Name); WriteCanonical(writer, property.Value); }
            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in element.EnumerateArray()) WriteCanonical(writer, item);
            writer.WriteEndArray();
        }
        else element.WriteTo(writer);
    }
}

/// <summary>StartKey is the kernel's stable birth identity; wall time is only the legacy handle projection. Linux wall-time conversion can differ between observing processes.</summary>
public sealed record NativeProcessIdentity(int ProcessId, long StartTimeUtcTicks, string BootId, string StartKey);

public sealed record NativeLaunchRecord
{
    public int Version { get; init; } = NativeLaunchProtocol.Version;
    public required string SpecHash { get; init; }
    public required string SpoolKey { get; init; }
    public SandboxLaunchIdentity? Identity { get; init; }
    public required string Host { get; init; }
    public required string BootId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset Deadline { get; init; }
}

/// <summary>The immutable native identity projected into the app's durable handle. The receipt remains required; this does not turn check-then-signal into a kernel-atomic operation.</summary>
public sealed record NativeLaunchReference(int Version, string SpecHash, SandboxLaunchIdentity? Identity, NativeProcessIdentity Execution);

public sealed record NativeLaunchCommitment(string SpecHash, NativeProcessIdentity Broker);

public sealed record NativeLaunchReceipt
{
    public required string SpecHash { get; init; }
    public required NativeProcessIdentity Broker { get; init; }
    public NativeProcessIdentity? Guardian { get; init; }
    public NativeProcessIdentity? Execution { get; init; }
    public required string State { get; init; }
    public string? Problem { get; init; }
    public string? EgressNetnsKey { get; init; }
    public string? CgroupRunKey { get; init; }
    public SandboxConfinement? Confinement { get; init; }
}

/// <summary>Private pipe only. Raw argv, environment and configuration never enter a receipt.</summary>
public sealed record NativeLaunchInvocation
{
    public required SandboxSpec Spec { get; init; }
    public required IReadOnlyList<string> ReadOnlyPaths { get; init; }
    public SandboxCaptureBudget? CaptureBudget { get; init; }
    public required string Command { get; init; }
    public required string[] Args { get; init; }
    public required string WorkingDirectory { get; init; }
    public required Dictionary<string, string?> Environment { get; init; }
    public string? EgressNetnsKey { get; init; }
    public string? CgroupRunKey { get; init; }
    public SandboxConfinement? Confinement { get; init; }
}

public sealed record NativeLaunchStop(string Reason, DateTimeOffset At);
