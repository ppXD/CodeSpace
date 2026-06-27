namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// The many-to-many binding between an Agent persona and a Skill it carries — the relational replacement for
/// the (now-dormant) <c>AgentDefinition.SkillsJson</c> blob. A join row exists iff the agent is bound to the
/// skill; unbinding hard-deletes the row (a pure association with no history value). Indexed both ways so the
/// two UI questions are index hits, not blob scans: "which skills does this agent carry" (by agent) and
/// "which agents use this skill" (by skill).
///
/// <para>Both sides FK to their definition; those entities soft-delete (DeletedDate), so a binding is never
/// orphaned by a hard delete. A unique (agent, skill) pair keeps the same skill from binding twice.</para>
/// </summary>
public class AgentSkillBinding : IEntity<Guid>
{
    public Guid Id { get; set; }

    public Guid AgentDefinitionId { get; set; }

    public Guid SkillDefinitionId { get; set; }

    public DateTimeOffset CreatedDate { get; set; }

    public Guid CreatedBy { get; set; }
}
