namespace CodeSpace.Messages.Enums;

/// <summary>How a <c>SkillDefinition</c> came to exist — mirrors <see cref="AgentDefinitionOrigin"/>.</summary>
public enum SkillDefinitionOrigin
{
    /// <summary>Authored in CodeSpace by a user.</summary>
    Authored,

    /// <summary>Imported from a pack (a git source); re-syncable via its pack + source path.</summary>
    Imported,
}
