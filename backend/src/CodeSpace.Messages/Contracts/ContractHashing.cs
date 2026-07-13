using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeSpace.Messages.Contracts;

/// <summary>
/// THE canonical contract hash (v4.1-B / P1b): a versioned, self-describing digest of an EFFECTIVE contract's
/// canonical bytes — the value receipts, co-signs, Carry authorizations, and ReceiptAdmission all bind to. Format:
/// <c>sha256/canonical-json-v1:&lt;64 lower hex&gt;</c> — a stored hash names its own algorithm, so a future v2 can
/// never be confused with (or collide into) v1; the algorithm id is also fed into the digest as a domain separator.
/// Canonicalization rides <c>ToolCallKey.Canonicalize</c> (ordinal key sort, whitespace-free, LOSSLESS number
/// normalization — the exactly-once machinery's pinned transform), so the same logical contract arriving with
/// reordered keys or respelled number tokens hashes identically. Callers hash the EFFECTIVE contract — after
/// operator/server clamps — and must exclude volatile identity (plan row ids, timestamps): the hash names WHAT is
/// owed, never WHEN or WHERE it was recorded.
/// </summary>
public static class ContractHashing
{
    public const string Algorithm = "sha256/canonical-json-v1";

    /// <summary>Hash an effective contract's JSON. Self-describing (<c>{Algorithm}:{hex}</c>).</summary>
    public static string Hash(JsonElement effectiveContract)
    {
        var canonical = Agents.ToolCallKey.Canonicalize(effectiveContract);

        return $"{Algorithm}:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{Algorithm}\n{canonical}")))}";
    }

    /// <summary>Convenience over a serializable contract object (serialized with the given options, then canonicalized — so property ORDER in the object never matters).</summary>
    public static string Hash<T>(T effectiveContract, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(effectiveContract, options));

        return Hash(doc.RootElement);
    }
}
