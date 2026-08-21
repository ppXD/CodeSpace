namespace CodeSpace.Api.Http;

/// <summary>Canonical response headers for an exact bounded Workflow Run record payload read.</summary>
public static class RunRecordPayloadHttpHeaders
{
    public const string RunId = "X-CodeSpace-Workflow-Run-Id";
    public const string RecordId = "X-CodeSpace-Workflow-Run-Record-Id";
    public const string Sequence = "X-CodeSpace-Workflow-Run-Record-Sequence";
    public const string Offset = "X-CodeSpace-Workflow-Run-Record-Payload-Offset";
    public const string NextOffset = "X-CodeSpace-Workflow-Run-Record-Payload-Next-Offset";
    public const string TotalBytes = "X-CodeSpace-Workflow-Run-Record-Payload-Total-Bytes";
    public const string ContentType = "X-CodeSpace-Workflow-Run-Record-Payload-Content-Type";

    public static readonly string[] RangeResponseHeaders = { RunId, RecordId, Sequence, Offset, NextOffset, TotalBytes, ContentType };
}
