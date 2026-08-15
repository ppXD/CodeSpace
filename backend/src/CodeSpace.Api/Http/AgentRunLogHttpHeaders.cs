namespace CodeSpace.Api.Http;

/// <summary>One canonical vocabulary for the bounded Agent Run log byte-range response.</summary>
public static class AgentRunLogHttpHeaders
{
    public const string Offset = "X-CodeSpace-Log-Offset";
    public const string NextOffset = "X-CodeSpace-Log-Next-Offset";
    public const string TotalBytes = "X-CodeSpace-Log-Total-Bytes";
    public const string HasMore = "X-CodeSpace-Log-Has-More";
    public const string Revision = "X-CodeSpace-Log-Revision";
    public const string ContentType = "X-CodeSpace-Log-Content-Type";
    public const string ContentEncoding = "X-CodeSpace-Log-Content-Encoding";

    public static readonly string[] RangeResponseHeaders = { Offset, NextOffset, TotalBytes, HasMore, Revision, ContentType, ContentEncoding };
}
