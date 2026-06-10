namespace CodeSpace.Messages.Enums;

/// <summary>How an <c>AgentDefinition</c> (Agent persona) came to exist.</summary>
public enum AgentDefinitionOrigin
{
    /// <summary>Authored in CodeSpace by a user.</summary>
    Authored,

    /// <summary>Imported from an agent pack (a git source); re-syncable via its pack + source path.</summary>
    Imported,
}
