using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;
using Shouldly;

namespace CodeSpace.UnitTests.Contracts;

/// <summary>
/// Harness data-plane v1 pins. Every later capture slice writes against this vocabulary, so a rename here is a
/// VISIBLE decision rather than a silent wire break — and a projection can never claim more fidelity than the
/// native records it was folded from actually carry.
/// </summary>
[Trait("Category", "Unit")]
public sealed class HarnessDataPlaneContractTests
{
    [Fact]
    public void The_harness_data_plane_names_are_registered_run_owned_and_kinded()
    {
        WorkflowRunDataNames.HarnessDescriptor.ShouldBe("workflow_run_harness_descriptor");
        WorkflowRunDataNames.RunnerHandle.ShouldBe("workflow_run_runner_handle");
        WorkflowRunDataNames.All.ShouldContain(WorkflowRunDataNames.HarnessDescriptor);
        WorkflowRunDataNames.All.ShouldContain(WorkflowRunDataNames.RunnerHandle);
        WorkflowRunDataNames.IsRunOwned(WorkflowRunDataNames.HarnessDescriptor).ShouldBeTrue();
        WorkflowRunDataNames.IsRunOwned(WorkflowRunDataNames.RunnerHandle).ShouldBeTrue();

        WorkflowRunDataOwnerKinds.HarnessDescriptor.ShouldBe("harness-descriptor");
        WorkflowRunDataOwnerKinds.RunnerHandle.ShouldBe("runner-handle");
        WorkflowRunDataOwnerKinds.IsSupported(WorkflowRunDataOwnerKinds.HarnessDescriptor).ShouldBeTrue();
        WorkflowRunDataOwnerKinds.IsSupported(WorkflowRunDataOwnerKinds.RunnerHandle).ShouldBeTrue();
    }

    [Fact]
    public void Every_registered_table_name_is_run_owned_and_has_a_registered_owner_kind()
    {
        foreach (var tableName in WorkflowRunDataNames.All)
        {
            WorkflowRunDataNames.IsRunOwned(tableName).ShouldBeTrue();

            var ownerKind = tableName[WorkflowRunDataNames.Prefix.Length..].Replace('_', '-');

            WorkflowRunDataOwnerKinds.IsSupported(ownerKind).ShouldBeTrue($"'{tableName}' is registered as run-owned but '{ownerKind}' is not a registered owner noun, so nothing stored in it could ever be referenced");
        }
    }

    [Fact]
    public void Only_the_current_contract_version_is_accepted_by_the_new_records()
    {
        WorkflowRunDataContract.IsSupported(WorkflowRunDataContract.CurrentVersion).ShouldBeTrue();
        WorkflowRunDataContract.IsSupported(WorkflowRunDataContract.CurrentVersion + 1).ShouldBeFalse();

        ValidNativeRecord().Validate().ShouldBeEmpty();
        ValidSemanticEvent().Validate().ShouldBeEmpty();

        (ValidNativeRecord() with { ContractVersion = 99 }).Validate().ShouldContain(error => error.Contains("contractVersion", StringComparison.Ordinal));
        (ValidSemanticEvent() with { ContractVersion = 99 }).Validate().ShouldContain(error => error.Contains("contractVersion", StringComparison.Ordinal));
    }

    [Fact]
    public void The_native_record_channel_vocabulary_is_pinned()
    {
        Enum.GetNames<NativeRecordChannel>().ShouldBe(new[]
        {
            "Stdout", "Stderr", "Protocol", "Control", "SessionState", "ModelWire", "ToolWire", "Hook", "Metric", "Debug",
        });

        JsonSerializer.Serialize(NativeRecordChannel.SessionState, AgentJson.Options).ShouldBe("\"SessionState\"");
    }

    [Fact]
    public void The_projection_quality_vocabulary_is_pinned_and_only_two_states_are_exact()
    {
        Enum.GetNames<SemanticProjectionQuality>().ShouldBe(new[] { "Exact", "RedactedExact", "Derived", "Heuristic", "Unknown" });

        SemanticProjectionQuality.Exact.IsExactlyGrounded().ShouldBeTrue();
        SemanticProjectionQuality.RedactedExact.IsExactlyGrounded().ShouldBeTrue();
        SemanticProjectionQuality.Derived.IsExactlyGrounded().ShouldBeFalse();
        SemanticProjectionQuality.Heuristic.IsExactlyGrounded().ShouldBeFalse();
        SemanticProjectionQuality.Unknown.IsExactlyGrounded().ShouldBeFalse();

        JsonSerializer.Serialize(SemanticProjectionQuality.Heuristic, AgentJson.Options).ShouldBe("\"Heuristic\"");
    }

    [Fact]
    public void The_remaining_harness_data_plane_vocabularies_are_pinned()
    {
        Enum.GetNames<HarnessCapability>().ShouldBe(new[]
        {
            "None", "StructuredNativeStream", "StandardOutput", "StandardError", "ExactModelTelemetry",
            "ExactToolTelemetry", "ResumeOrFork", "Compaction", "SteerOrAbort", "StateExportImport", "ProtocolHeartbeat",
        });

        ((int)HarnessCapability.StructuredNativeStream).ShouldBe(1, "the flag VALUES ride on persisted descriptors — reordering them silently rewrites what an old run could do");
        ((int)HarnessCapability.ProtocolHeartbeat).ShouldBe(1 << 9);

        Enum.GetNames<HarnessNativeProtocolKind>().ShouldBe(new[] { "CliJsonl", "CliText", "JsonRpc", "Sdk", "Remote" });
        Enum.GetNames<NativeRecordRedaction>().ShouldBe(new[] { "None", "Masked", "Withheld" });
        Enum.GetNames<NativeRecordPayloadEncoding>().ShouldBe(new[] { "Utf8", "Base64" });
        Enum.GetNames<SemanticEventNecessity>().ShouldBe(new[] { "Required", "Ignorable" });
    }

    [Fact]
    public void A_descriptor_reads_its_capabilities_as_a_set_and_never_answers_yes_to_the_empty_one()
    {
        var descriptor = ValidDescriptor();

        descriptor.Validate().ShouldBeEmpty();
        descriptor.Supports(HarnessCapability.StructuredNativeStream).ShouldBeTrue();
        descriptor.Supports(HarnessCapability.ExactModelTelemetry).ShouldBeTrue();
        descriptor.Supports(HarnessCapability.Compaction).ShouldBeFalse();
        descriptor.Supports(HarnessCapability.None).ShouldBeFalse("HasFlag(None) is vacuously true — asking for nothing must never read as a declared capability");
    }

    [Fact]
    public void A_descriptor_with_a_malformed_identity_or_a_key_that_is_both_config_and_secret_fails_closed()
    {
        var descriptor = ValidDescriptor() with
        {
            TypeKey = "claude-code",
            AdapterVersion = " ",
            SupportedNativeVersions = Array.Empty<string>(),
            Capabilities = HarnessCapability.None,
            SecretSchema = new[] { new HarnessSettingDescriptor { Key = "model", Required = true } },
        };

        var errors = descriptor.Validate();

        errors.ShouldContain(error => error.Contains("typeKey", StringComparison.Ordinal));
        errors.ShouldContain(error => error.Contains("adapterVersion", StringComparison.Ordinal));
        errors.ShouldContain(error => error.Contains("supportedNativeVersions", StringComparison.Ordinal));
        errors.ShouldContain(error => error.Contains("capabilities", StringComparison.Ordinal));
        errors.ShouldContain(error => error.Contains("never both", StringComparison.Ordinal));
    }

    /// <summary>
    /// The generation FLOOR, on the side of the seam that validates a key rather than the one that builds it. The
    /// database checks <c>harness_type_key</c> against <c>/v[1-9][0-9]*</c> (migration 0137), so a zero major and a
    /// zero-padded one are not representable keys at all — a descriptor that accepted them would declare an adapter
    /// identity no row could ever carry, and this type is what a snapshot will be validated through.
    /// </summary>
    [Theory]
    [InlineData("codex-cli/v1", true)]
    [InlineData("claude-code/v2", true)]
    [InlineData("codex-cli/v0", false)]
    [InlineData("codex-cli/v01", false)]
    public void A_descriptor_identity_names_a_generation_the_database_can_actually_store(string typeKey, bool usable)
    {
        var errors = (ValidDescriptor() with { TypeKey = typeKey }).Validate();

        errors.Any(error => error.Contains("typeKey", StringComparison.Ordinal)).ShouldBe(!usable,
            customMessage: "the descriptor and the harness-execution key check must define ONE quantity, so a major below 1 — or one written with a leading zero — has to be refused here too");
    }

    [Theory]
    [InlineData(true, true, "exactly one")]
    [InlineData(false, false, "exactly one")]
    [InlineData(true, false, null)]
    [InlineData(false, true, null)]
    public void A_native_record_carries_its_payload_inline_or_by_reference_but_never_both_or_neither(bool inline, bool byReference, string? expectedError)
    {
        var record = ValidNativeRecord() with
        {
            InlinePayload = inline ? "{\"type\":\"assistant\"}" : null,
            PayloadRef = byReference ? ValidPayloadRef() : null,
        };

        var errors = record.Validate();

        if (expectedError is null) errors.ShouldBeEmpty();
        else errors.ShouldContain(error => error.Contains(expectedError, StringComparison.Ordinal));
    }

    [Fact]
    public void A_withheld_payload_may_only_be_a_reference_and_a_reference_must_agree_on_its_bytes()
    {
        var withheldInline = ValidNativeRecord() with { Redaction = NativeRecordRedaction.Withheld };

        withheldInline.Validate().ShouldContain(error => error.Contains("withheld", StringComparison.OrdinalIgnoreCase));

        var disagreeing = ValidNativeRecord() with
        {
            InlinePayload = null,
            PayloadRef = ValidPayloadRef() with { SizeBytes = 7 },
        };

        disagreeing.Validate().ShouldContain(error => error.Contains("payloadRef", StringComparison.Ordinal));
    }

    [Fact]
    public void A_native_record_round_trips_on_the_wire()
    {
        var record = ValidNativeRecord();

        var json = JsonSerializer.Serialize(record, AgentJson.Options);
        var roundTrip = JsonSerializer.Deserialize<NativeRecordV1>(json, AgentJson.Options);

        roundTrip.ShouldNotBeNull();
        roundTrip!.Channel.ShouldBe(NativeRecordChannel.Stdout);
        roundTrip.Encoding.ShouldBe(NativeRecordPayloadEncoding.Utf8);
        roundTrip.IsFinal.ShouldBeTrue();
        JsonSerializer.Serialize(roundTrip, AgentJson.Options).ShouldBe(json);
    }

    [Theory]
    [InlineData(SemanticProjectionQuality.Exact, false)]
    [InlineData(SemanticProjectionQuality.RedactedExact, false)]
    [InlineData(SemanticProjectionQuality.Derived, true)]
    [InlineData(SemanticProjectionQuality.Heuristic, true)]
    [InlineData(SemanticProjectionQuality.Unknown, true)]
    public void A_projection_with_no_source_record_can_never_claim_exactness(SemanticProjectionQuality quality, bool isAcceptable)
    {
        var ungrounded = ValidSemanticEvent() with { ProjectionQuality = quality, SourceNativeRecordIds = Array.Empty<Guid>() };

        var errors = ungrounded.Validate();

        if (isAcceptable) errors.ShouldBeEmpty();
        else errors.ShouldContain(error => error.Contains("projectionQuality", StringComparison.Ordinal));
    }

    [Fact]
    public void A_semantic_event_needs_a_uri_event_type_and_an_execution_it_belongs_to()
    {
        var event1 = ValidSemanticEvent() with { EventType = "assistant-message", ExecutionId = Guid.Empty, EventSchemaVersion = 0 };

        var errors = event1.Validate();

        errors.ShouldContain(error => error.Contains("eventType", StringComparison.Ordinal));
        errors.ShouldContain(error => error.Contains("executionId", StringComparison.Ordinal));
        errors.ShouldContain(error => error.Contains("eventSchemaVersion", StringComparison.Ordinal));
    }

    /// <summary>
    /// The DECLARED envelope's own shape, which is all this can pin: <see cref="RunnerHandleEnvelope"/> has no
    /// production reader or writer yet, and the handle production actually persists — <c>SandboxHandle</c> — hoists both
    /// a <c>required</c> pid and a <c>required</c> spool path. So this asserts the target shape is still the target, NOT
    /// that any persisted handle keeps backend detail out of shared fields; nothing today does.
    /// </summary>
    [Fact]
    public void The_declared_envelope_shape_hoists_no_pid_even_though_todays_persisted_handle_does()
    {
        var handle = ValidHandle();

        handle.Validate().ShouldBeEmpty();
        handle.Locator.GetProperty("processId").GetInt32().ShouldBe(4242);

        var shared = typeof(RunnerHandleEnvelope).GetProperties().Select(property => property.Name).ToList();

        shared.ShouldNotContain("ProcessId", "a local pid on the SHARED envelope is exactly what makes a remote backend unrepresentable");
        shared.ShouldNotContain("SpoolDirectory", "an on-disk spool path belongs in the local runner's own locator payload");
    }

    [Fact]
    public void A_runner_handle_without_a_locator_or_a_fence_fails_closed()
    {
        var handle = ValidHandle() with
        {
            Kind = " ",
            SchemaVersion = 0,
            ExecutionId = Guid.Empty,
            Locator = JsonDocument.Parse("\"local\"").RootElement.Clone(),
            ClaimOwnerId = "",
            FenceToken = 0,
        };

        var errors = handle.Validate();

        errors.ShouldContain(error => error.Contains("kind", StringComparison.Ordinal));
        errors.ShouldContain(error => error.Contains("schemaVersion", StringComparison.Ordinal));
        errors.ShouldContain(error => error.Contains("executionId", StringComparison.Ordinal));
        errors.ShouldContain(error => error.Contains("locator", StringComparison.Ordinal));
        errors.ShouldContain(error => error.Contains("claimOwnerId", StringComparison.Ordinal));
        errors.ShouldContain(error => error.Contains("fenceToken", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(NativeRecordRedaction.None, WorkflowRunCaptureCompleteness.Exact, null)]
    [InlineData(NativeRecordRedaction.None, WorkflowRunCaptureCompleteness.Partial, null)]
    [InlineData(NativeRecordRedaction.Masked, WorkflowRunCaptureCompleteness.RedactedExact, null)]
    [InlineData(NativeRecordRedaction.Masked, WorkflowRunCaptureCompleteness.Partial, null)]
    [InlineData(NativeRecordRedaction.Masked, WorkflowRunCaptureCompleteness.Exact, "masked")]
    [InlineData(NativeRecordRedaction.Withheld, WorkflowRunCaptureCompleteness.Unavailable, null)]
    [InlineData(NativeRecordRedaction.Withheld, WorkflowRunCaptureCompleteness.Exact, "withheld")]
    [InlineData(NativeRecordRedaction.Withheld, WorkflowRunCaptureCompleteness.RedactedExact, "withheld")]
    [InlineData(NativeRecordRedaction.Withheld, WorkflowRunCaptureCompleteness.Partial, "withheld")]
    [InlineData(NativeRecordRedaction.Withheld, WorkflowRunCaptureCompleteness.Corrupt, "withheld")]
    public void A_redacted_payload_can_never_reference_content_claiming_more_completeness_than_survived_it(NativeRecordRedaction redaction, WorkflowRunCaptureCompleteness completeness, string? expectedError)
    {
        var record = ValidNativeRecord() with
        {
            InlinePayload = null,
            PayloadRef = ValidPayloadRef() with { Completeness = completeness },
            Redaction = redaction,
        };

        var errors = record.Validate();

        if (expectedError is null) errors.ShouldBeEmpty();
        else errors.ShouldContain(error => error.Contains(expectedError, StringComparison.Ordinal));
    }

    [Fact]
    public void A_withheld_frame_that_validates_can_never_present_a_strictly_readable_payload()
    {
        var withheld = ValidNativeRecord() with
        {
            InlinePayload = null,
            PayloadRef = ValidPayloadRef() with { Completeness = WorkflowRunCaptureCompleteness.Unavailable },
            Redaction = NativeRecordRedaction.Withheld,
        };

        withheld.Validate().ShouldBeEmpty();
        withheld.PayloadRef!.Completeness.IsStrictlyReadable().ShouldBeFalse("a frame that was deliberately never captured must never enter a strict agent, resume, oracle or completion read");
    }

    [Fact]
    public void An_unrecognised_redaction_or_encoding_is_rejected_rather_than_silently_skipping_the_rules_hanging_off_it()
    {
        var record = ValidNativeRecord() with { Redaction = (NativeRecordRedaction)99, Encoding = (NativeRecordPayloadEncoding)99 };

        var errors = record.Validate();

        errors.ShouldContain(error => error.Contains("redaction", StringComparison.Ordinal), "an unknown redaction that validates clean skips the withheld and masked rules that hang off it");
        errors.ShouldContain(error => error.Contains("encoding", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_harness_data_plane_record_round_trips_to_a_value_equal_copy()
    {
        AssertRoundTripsByValue(ValidNativeRecord());
        AssertRoundTripsByValue(ValidSemanticEvent());
        AssertRoundTripsByValue(ValidDescriptor());
        AssertRoundTripsByValue(ValidHandle());
    }

    [Fact]
    public void A_runner_handle_compares_by_value_including_the_locator_and_the_fence()
    {
        var executionId = Guid.NewGuid();
        var left = ValidHandle() with { ExecutionId = executionId, Locator = LocalLocator() };
        var right = ValidHandle() with { ExecutionId = executionId, Locator = LocalLocator() };

        left.ShouldBe(right, "two handles built from the same bytes must be equal, or the stored-claim-versus-my-claim comparison this record exists for always answers 'changed'");
        left.GetHashCode().ShouldBe(right.GetHashCode());

        (left with { FenceToken = left.FenceToken + 1 }).ShouldNotBe(left, "raising the fence is the takeover — equality that ignored it would let a lost claim read as the current one");
        (left with { Locator = JsonDocument.Parse("{\"processId\":9}").RootElement.Clone() }).ShouldNotBe(left);
    }

    [Fact]
    public void A_descriptor_and_a_semantic_event_compare_their_lists_element_wise()
    {
        ValidDescriptor().ShouldBe(ValidDescriptor(), "a descriptor is snapshotted to answer 'is the adapter that ran still the adapter I have?'");
        ValidDescriptor().GetHashCode().ShouldBe(ValidDescriptor().GetHashCode());
        (ValidDescriptor() with { SupportedNativeVersions = new[] { "1.0.60" } }).ShouldNotBe(ValidDescriptor());
        (ValidDescriptor() with { SecretSchema = Array.Empty<HarnessSettingDescriptor>() }).ShouldNotBe(ValidDescriptor());

        var sourceId = Guid.NewGuid();
        var grounded = ValidSemanticEvent() with { SourceNativeRecordIds = new[] { sourceId } };

        (grounded with { SourceNativeRecordIds = new List<Guid> { sourceId } }).ShouldBe(grounded, "the source list IS the event's grounding — comparing it by reference makes two identically grounded events unequal");
        (grounded with { SourceNativeRecordIds = new[] { Guid.NewGuid() } }).ShouldNotBe(grounded);
    }

    [Fact]
    public void Formatting_a_handle_or_a_native_record_never_prints_the_opaque_bytes_it_carries()
    {
        var handle = ValidHandle() with { Locator = JsonDocument.Parse("{\"authToken\":\"sk-live-SENTINEL\"}").RootElement.Clone() };

        var handleText = handle.ToString();

        handleText.ShouldNotContain("SENTINEL", customMessage: "the locator is where runners are told to put backend material, so one interpolated log line spills it");
        handleText.ShouldContain(handle.ClaimOwnerId, customMessage: "the claim is the whole reason a handle gets logged — redacting the locator must not blind the log");
        handleText.ShouldContain(handle.FenceToken.ToString());

        var recordText = (ValidNativeRecord() with { InlinePayload = "{\"authorization\":\"Bearer SENTINEL\"}" }).ToString();

        recordText.ShouldNotContain("SENTINEL", customMessage: "an inline ModelWire frame is the model request wire, headers included — its Redaction field exists because those bytes may need masking");
        recordText.ShouldContain(ValidNativeRecord().Digest, customMessage: "the digest and size identify the payload without reproducing it");
    }

    [Fact]
    public void The_records_that_hand_write_their_equality_or_printing_pin_their_field_count()
    {
        const string byHand = "a field added here must be added by hand to Equals and GetHashCode, or it silently drops out of every comparison";

        typeof(RunnerHandleEnvelope).GetProperties().Length.ShouldBe(10, $"{byHand} — and to PrintMembers, or a new opaque field starts reaching logs");
        typeof(HarnessDescriptor).GetProperties().Length.ShouldBe(7, byHand);
        typeof(AgentSemanticEventV1).GetProperties().Length.ShouldBe(16, byHand);
        typeof(NativeRecordV1).GetProperties().Length.ShouldBe(20, "NativeRecordV1 keeps the generated equality but prints its members by hand — a field added here must be added to PrintMembers, or a new payload-bearing field starts reaching logs");
    }

    private static void AssertRoundTripsByValue<T>(T original) where T : notnull
    {
        var json = JsonSerializer.Serialize(original, AgentJson.Options);
        var roundTrip = JsonSerializer.Deserialize<T>(json, AgentJson.Options);

        roundTrip.ShouldBe(original, $"a {typeof(T).Name} that does not equal its own round trip makes every later 'has this changed?' read answer yes");
        roundTrip!.GetHashCode().ShouldBe(original.GetHashCode(), $"{typeof(T).Name} equality and hashing must agree, or an equal value silently misses as a dictionary key");
    }

    private static JsonElement LocalLocator() => JsonDocument.Parse("{\"processId\":4242,\"spoolDirectory\":\"/var/spool/run\"}").RootElement.Clone();

    private static HarnessDescriptor ValidDescriptor() => new()
    {
        TypeKey = "claude-code/v2",
        AdapterVersion = "2.0.0",
        SupportedNativeVersions = new[] { "1.0.60", "1.0.61" },
        NativeProtocol = HarnessNativeProtocolKind.CliJsonl,
        Capabilities = HarnessCapability.StructuredNativeStream | HarnessCapability.StandardOutput | HarnessCapability.StandardError | HarnessCapability.ExactModelTelemetry,
        ConfigSchema = new[] { new HarnessSettingDescriptor { Key = "model", Required = false } },
        SecretSchema = new[] { new HarnessSettingDescriptor { Key = "ANTHROPIC_API_KEY", Required = true } },
    };

    private static NativeRecordV1 ValidNativeRecord() => new()
    {
        ContractVersion = WorkflowRunDataContract.CurrentVersion,
        RecordId = Guid.NewGuid(),
        StreamId = Guid.NewGuid(),
        Ordinal = 12,
        Channel = NativeRecordChannel.Stdout,
        NativeType = "assistant",
        IngestedAt = DateTimeOffset.UnixEpoch,
        ByteOffset = 4096,
        ByteLength = 20,
        InlinePayload = "{\"type\":\"assistant\"}",
        DigestAlgorithm = WorkflowRunDataContract.Sha256Algorithm,
        Digest = new string('a', 64),
        SizeBytes = 20,
        Encoding = NativeRecordPayloadEncoding.Utf8,
        Redaction = NativeRecordRedaction.None,
        IsFinal = true,
    };

    private static WorkflowRunArtifactRefV1 ValidPayloadRef() => new()
    {
        ContractVersion = WorkflowRunDataContract.CurrentVersion,
        WorkflowRunId = Guid.NewGuid(),
        OwnerKind = WorkflowRunDataOwnerKinds.NativeRecord,
        OwnerId = "native-record-12",
        Role = "native.frame",
        ArtifactId = Guid.NewGuid(),
        DigestAlgorithm = WorkflowRunDataContract.Sha256Algorithm,
        Digest = new string('a', 64),
        SizeBytes = 20,
        Completeness = WorkflowRunCaptureCompleteness.Exact,
    };

    private static AgentSemanticEventV1 ValidSemanticEvent() => new()
    {
        ContractVersion = WorkflowRunDataContract.CurrentVersion,
        EventId = Guid.NewGuid(),
        EventType = "https://codespace.dev/agent/v1/assistant-message",
        EventSchemaVersion = 1,
        SourceNativeRecordIds = new[] { Guid.NewGuid() },
        ExecutionId = Guid.NewGuid(),
        Necessity = SemanticEventNecessity.Required,
        ProjectionQuality = SemanticProjectionQuality.Exact,
    };

    private static RunnerHandleEnvelope ValidHandle() => new()
    {
        Kind = "local",
        SchemaVersion = 1,
        ExecutionId = Guid.NewGuid(),
        HostAffinity = "worker-3",
        Locator = LocalLocator(),
        Deadline = DateTimeOffset.UnixEpoch.AddHours(1),
        ClaimOwnerId = "worker-3/observer-1",
        FenceToken = 1,
    };
}
