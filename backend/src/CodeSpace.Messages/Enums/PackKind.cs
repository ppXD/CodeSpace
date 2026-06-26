namespace CodeSpace.Messages.Enums;

/// <summary>Where a <c>Pack</c> (an importable library of agents/skills) is sourced from.</summary>
public enum PackKind
{
    /// <summary>A GitHub repository referenced by <c>owner/repo</c>.</summary>
    Github,

    /// <summary>Any git repository referenced by its full clone URL.</summary>
    GitUrl,

    /// <summary>The synthetic per-team pack that holds locally-authored artifacts (no remote source).</summary>
    Custom,
}
