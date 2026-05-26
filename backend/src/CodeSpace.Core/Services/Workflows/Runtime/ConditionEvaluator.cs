using System.Globalization;
using System.Text.Json;

namespace CodeSpace.Core.Services.Workflows.Runtime;

/// <summary>
/// Minimal condition evaluator for logic.if / edge conditions. Supports a deliberately narrow
/// expression grammar so it stays predictable and sandboxed — no arbitrary code, no I/O:
///
///   &lt;value&gt; &lt;op&gt; &lt;value&gt;
///   where:
///     value := {{ref}} | "literal string" | 'literal string' | number | true | false | null
///     op    := == | != | &gt; | &gt;= | &lt; | &lt;= | contains | startsWith | endsWith | is_empty | is_not_empty
///
/// `is_empty` and `is_not_empty` are unary (no right-hand value). Boolean truthiness
/// (`{{some.value}}` on its own) is supported as a degenerate case — the value is coerced
/// using the same JS-truthy rules the engine uses for legacy edge conditions.
///
/// Examples:
///   {{trigger.number}} &gt; 100
///   {{nodes.fetch.outputs.files}} is_not_empty
///   {{trigger.author}} == "alice"
///   {{trigger.state}} contains "open"
///
/// Anything more complex (and/or, parens, nested expressions) is a logic.switch node OR a
/// future logic.expression node — keeping this grammar tiny means the editor can generate
/// it from a UI form without needing a real parser.
/// </summary>
public static class ConditionEvaluator
{
    private const string OpEq = "==";
    private const string OpNeq = "!=";
    private const string OpGt = ">";
    private const string OpGte = ">=";
    private const string OpLt = "<";
    private const string OpLte = "<=";
    private const string OpContains = "contains";
    private const string OpStartsWith = "startsWith";
    private const string OpEndsWith = "endsWith";
    private const string OpIsEmpty = "is_empty";
    private const string OpIsNotEmpty = "is_not_empty";

    private static readonly string[] BinaryOps = { OpEq, OpNeq, OpGte, OpLte, OpGt, OpLt, OpContains, OpStartsWith, OpEndsWith };
    private static readonly string[] UnaryOps  = { OpIsEmpty, OpIsNotEmpty };

    public static bool Evaluate(string expression, NodeRunScope scope)
    {
        if (string.IsNullOrWhiteSpace(expression)) return false;

        var trimmed = expression.Trim();

        // Try unary first (suffix operators).
        foreach (var op in UnaryOps)
        {
            var marker = " " + op;
            if (trimmed.EndsWith(marker, StringComparison.Ordinal))
            {
                var left = trimmed[..^marker.Length].Trim();
                var leftValue = Resolve(left, scope);
                return op == OpIsEmpty ? IsEmpty(leftValue) : !IsEmpty(leftValue);
            }
        }

        // Try binary ops. Longest-match first so `>=` wins over `>`.
        foreach (var op in BinaryOps.OrderByDescending(o => o.Length))
        {
            var marker = " " + op + " ";
            var idx = trimmed.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) continue;

            var leftRaw = trimmed[..idx].Trim();
            var rightRaw = trimmed[(idx + marker.Length)..].Trim();

            var left = Resolve(leftRaw, scope);
            var right = Resolve(rightRaw, scope);

            return ApplyBinary(op, left, right);
        }

        // Bare truthiness fallback — `{{some.value}}` alone, or a literal.
        var value = Resolve(trimmed, scope);
        return Truthy(value);
    }

    /// <summary>Resolves a token. {{ref}} → walk scope; "literal" → string; number → number; etc.</summary>
    private static object? Resolve(string token, NodeRunScope scope)
    {
        if (token.Length >= 2 && token.StartsWith("{{") && token.EndsWith("}}"))
        {
            var path = token[2..^2].Trim();
            var value = VariableResolver.WalkPath(path, scope);
            return value.HasValue ? JsonElementToClr(value.Value) : null;
        }

        // Quoted string literal — handles both " and '
        if ((token.StartsWith('"') && token.EndsWith('"')) || (token.StartsWith('\'') && token.EndsWith('\'')))
            return token[1..^1];

        if (token.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (token.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        if (token.Equals("null", StringComparison.OrdinalIgnoreCase)) return null;

        // Try number — both int and double, invariant culture so commas don't surprise us.
        if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
        if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;

        // Bare identifier (no braces, no quotes) — treat as string. Lets people write
        // `{{trigger.state}} == open` without surrounding quotes for short tokens.
        return token;
    }

    private static bool ApplyBinary(string op, object? left, object? right)
    {
        return op switch
        {
            OpEq         => AreEqual(left, right),
            OpNeq        => !AreEqual(left, right),
            OpGt         => Compare(left, right) > 0,
            OpGte        => Compare(left, right) >= 0,
            OpLt         => Compare(left, right) < 0,
            OpLte        => Compare(left, right) <= 0,
            OpContains   => AsString(left).Contains(AsString(right), StringComparison.OrdinalIgnoreCase),
            OpStartsWith => AsString(left).StartsWith(AsString(right), StringComparison.OrdinalIgnoreCase),
            OpEndsWith   => AsString(left).EndsWith(AsString(right), StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static bool AreEqual(object? left, object? right)
    {
        if (left == null && right == null) return true;
        if (left == null || right == null) return false;

        // Numeric equality is special — int 5 == long 5L == double 5.0 should be true.
        if (IsNumeric(left) && IsNumeric(right))
            return Convert.ToDouble(left, CultureInfo.InvariantCulture) == Convert.ToDouble(right, CultureInfo.InvariantCulture);

        return AsString(left).Equals(AsString(right), StringComparison.Ordinal);
    }

    private static int Compare(object? left, object? right)
    {
        if (left == null && right == null) return 0;
        if (left == null) return -1;
        if (right == null) return 1;

        if (IsNumeric(left) && IsNumeric(right))
            return Convert.ToDouble(left, CultureInfo.InvariantCulture)
                .CompareTo(Convert.ToDouble(right, CultureInfo.InvariantCulture));

        return string.Compare(AsString(left), AsString(right), StringComparison.Ordinal);
    }

    private static bool Truthy(object? value) => value switch
    {
        null => false,
        bool b => b,
        string s => !string.IsNullOrEmpty(s),
        long l => l != 0,
        int i => i != 0,
        double d => d != 0.0,
        Array a => a.Length > 0,
        System.Collections.ICollection c => c.Count > 0,
        _ => true
    };

    private static bool IsEmpty(object? value) => value switch
    {
        null => true,
        string s => s.Length == 0,
        Array a => a.Length == 0,
        System.Collections.ICollection c => c.Count == 0,
        _ => false
    };

    private static bool IsNumeric(object value) => value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal;

    private static string AsString(object? value) => value switch
    {
        null => "",
        string s => s,
        bool b => b ? "true" : "false",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
    };

    private static object? JsonElementToClr(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => JsonElementToClr(p.Value)),
            JsonValueKind.Array => element.EnumerateArray().Select(JsonElementToClr).ToArray(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }
}
