using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.AliyunOss;

namespace CodeSpace.UnitTests.Workflows.Artifacts.Providers.AliyunOss;

/// <summary>
/// The four operator-supplied values that point the real-bucket conformance lane at a live OSS bucket, plus the
/// profile/credential projection that opens a driver over them.
///
/// This is the ONLY place the lane touches the secret. It is read from the environment, handed straight to a
/// <c>StorageCredentialHandle</c>, and never returned, formatted, logged, or written anywhere: nothing here holds a
/// logger, <see cref="AliyunOssRealBucketSettings.ToString"/> redacts, and the skip report names only the VARIABLES
/// that are unset - never a value. Nothing is committed: absent variables mean the lane skips.
/// </summary>
internal static class AliyunOssRealBucket
{
    /// <summary>Bucket the lane reads and writes. Named for the operator's own <c>BucketName</c> parameter.</summary>
    public const string BucketNameEnvVar = "CODESPACE_OSS_BUCKET_NAME";

    /// <summary>OSS endpoint host, e.g. <c>oss-cn-hangzhou.aliyuncs.com</c>. Must be a host that NAMES its region - the signing region is read out of it and the lane passes no region override.</summary>
    public const string EndpointEnvVar = "CODESPACE_OSS_ENDPOINT";

    /// <summary>AccessKey id. Named for the operator's own <c>OssAccessKeyId</c> parameter.</summary>
    public const string AccessKeyIdEnvVar = "CODESPACE_OSS_ACCESS_KEY_ID";

    /// <summary>AccessKey secret. Named for the operator's own <c>OssAccessKeySecret</c> parameter. A SECRET: never echoed by anything in this file.</summary>
    public const string AccessKeySecretEnvVar = "CODESPACE_OSS_ACCESS_KEY_SECRET";

    /// <summary>
    /// Root every object this lane can address lives under, so an abandoned run's leftovers are one prefix listing
    /// away and cost nothing to find. An operator pasting these variables once will not paste them again to rename a
    /// variable, so all five literals are pinned by <c>AliyunOssRealBucketLaneTests</c>.
    /// </summary>
    public const string KeyPrefixRoot = "codespace-conformance/";

    /// <summary>Every variable the lane reads, in the order the skip report names them. Pinned, so a fifth one cannot arrive unpinned.</summary>
    public static readonly string[] EnvVars = [BucketNameEnvVar, EndpointEnvVar, AccessKeyIdEnvVar, AccessKeySecretEnvVar];

    /// <summary>GitHub's own step-summary path, not ours to rename - a report written there survives xUnit's console capture and reaches the job-summary UI.</summary>
    private const string StepSummaryEnvVar = "GITHUB_STEP_SUMMARY";

    /// <summary>The four values when ALL are present, else null - the honest self-skip signal. Blank counts as absent.</summary>
    public static AliyunOssRealBucketSettings? TryRead(Func<string, string?> readEnv)
    {
        if (Unset(readEnv).Count != 0) return null;

        return new AliyunOssRealBucketSettings
        {
            BucketName = Value(readEnv, BucketNameEnvVar)!,
            Endpoint = Value(readEnv, EndpointEnvVar)!,
            AccessKeyId = Value(readEnv, AccessKeyIdEnvVar)!,
            AccessKeySecret = Value(readEnv, AccessKeySecretEnvVar)!
        };
    }

    /// <summary>The variable NAMES that are unset, so the skip line tells the operator exactly what to export. Never a value.</summary>
    public static IReadOnlyList<string> Unset(Func<string, string?> readEnv) => EnvVars.Where(name => Value(readEnv, name) == null).ToList();

    /// <summary>A per-run key namespace: the run's own prefix under <see cref="KeyPrefixRoot"/>, dated so leftovers say when they were abandoned.</summary>
    public static string RunKeyPrefix(DateTimeOffset startedAt) => $"{KeyPrefixRoot}{startedAt.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}-{Guid.NewGuid():N}/";

    /// <summary>
    /// The activation profile for one run. Only NON-secret material reaches it: the credential travels separately in a
    /// <see cref="StorageCredentialHandle"/>, because a profile's configuration is the half that gets persisted and
    /// surfaced. No <c>region</c> is set, so the endpoint host must name one - a host that does not is refused at
    /// activation with the driver's own message rather than signed with a wrong region.
    /// </summary>
    public static StorageProfileSnapshot Profile(AliyunOssRealBucketSettings settings, string keyPrefix) => new()
    {
        ProfileId = Guid.NewGuid(),
        ProfileRevision = 1,
        ProviderTypeKey = AliyunOssArtifactStorageDriverFactory.TypeKey,
        Configuration = JsonSerializer.SerializeToElement(new { endpoint = settings.Endpoint, bucket = settings.BucketName, keyPrefix })
    };

    /// <summary>The ephemeral credential handle the factory reads once during activation. The caller owns and disposes it.</summary>
    public static StorageCredentialHandle Credential(AliyunOssRealBucketSettings settings) =>
        new(JsonSerializer.SerializeToElement(new { accessKeyId = settings.AccessKeyId, accessKeySecret = settings.AccessKeySecret }));

    /// <summary>Surface the no-credentials skip LOUDLY as explicitly not-a-pass, so eight green no-ops can never read as "the real bucket conformed".</summary>
    public static void ReportSkipped(IReadOnlyList<string> unset) => ReportSkipped(unset, Environment.GetEnvironmentVariable(StepSummaryEnvVar));

    /// <summary>Testable core of <see cref="ReportSkipped(IReadOnlyList{string})"/> - explicit step-summary path, so a test pins the wording without mutating process env.</summary>
    internal static void ReportSkipped(IReadOnlyList<string> unset, string? stepSummaryPath) => Report(
        $"⏭️ Aliyun OSS real-bucket conformance NOT VERIFIED - skipped ({string.Join(", ", unset)} unset). A skip is NOT a pass: no bucket was reached, so nothing was proven against the real service.",
        stepSummaryPath);

    /// <summary>Surface objects this lane could not delete, naming the prefix an operator lists to find and purge them.</summary>
    public static void ReportLeftovers(string runKeyPrefix, int count, string detail) =>
        Report($"⚠️ Aliyun OSS real-bucket conformance left {count} object(s) behind under '{runKeyPrefix}' - delete that prefix. Cleanup fault: {detail}", Environment.GetEnvironmentVariable(StepSummaryEnvVar));

    private static void Report(string line, string? stepSummaryPath)
    {
        if (string.IsNullOrWhiteSpace(stepSummaryPath))
        {
            Console.WriteLine(line);
            return;
        }

        File.AppendAllText(stepSummaryPath, line + Environment.NewLine);
    }

    private static string? Value(Func<string, string?> readEnv, string name)
    {
        var raw = readEnv(name);

        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }
}

/// <summary>
/// One run's bucket coordinates and AccessKey. <see cref="ToString"/> is overridden because the compiler-generated
/// record one prints every property: a single interpolation of this value anywhere - a message, a log template, an
/// assertion - would otherwise publish the secret.
/// </summary>
internal sealed record AliyunOssRealBucketSettings
{
    public required string BucketName { get; init; }
    public required string Endpoint { get; init; }
    public required string AccessKeyId { get; init; }
    public required string AccessKeySecret { get; init; }

    public override string ToString() => $"AliyunOssRealBucketSettings {{ BucketName = {BucketName}, Endpoint = {Endpoint}, AccessKeyId = [REDACTED], AccessKeySecret = [REDACTED] }}";
}
