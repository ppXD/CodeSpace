namespace CodeSpace.Messages.Agents;

/// <summary>The frozen character-budget vocabulary for the supervisor's merge synthesis input. New projected runs stamp the resolved value into their node config; legacy/authored definitions normalize to the same bounded default.</summary>
public static class SupervisorSynthesisBudget
{
    public const int DefaultChars = 120_000;
    public const int MinChars = 2_000;
    public const int MaxChars = 1_000_000;

    public static int Normalize(int? authored) => authored is > 0 ? Math.Clamp(authored.Value, MinChars, MaxChars) : DefaultChars;
}
