using CodeSpace.Messages.Constants;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Persistence.EntityConfigurations;
using CodeSpace.Core.Services.Identity;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CodeSpace.Core.Persistence.Db;

public class CodeSpaceDbContext : DbContext, IUnitOfWork, IDataProtectionKeyContext
{
    private readonly ICurrentUser? _currentUser;
    private readonly IBotVisibility _botVisibility;

    public CodeSpaceDbContext(DbContextOptions<CodeSpaceDbContext> options, ICurrentUser? currentUser = null, IBotVisibility? botVisibility = null) : base(options)
    {
        _currentUser = currentUser;
        // Default to "exclude bots" when none is supplied (design-time / raw construction) — the
        // same safe default the global User query filter below enforces.
        _botVisibility = botVisibility ?? new BotVisibility();
    }

    public DbSet<User> User => Set<User>();
    public DbSet<Team> Team => Set<Team>();
    public DbSet<TeamMembership> TeamMembership => Set<TeamMembership>();
    public DbSet<ProviderInstance> ProviderInstance => Set<ProviderInstance>();
    public DbSet<Credential> Credential => Set<Credential>();
    public DbSet<Repository> Repository => Set<Repository>();
    public DbSet<RepositoryWebhook> RepositoryWebhook => Set<RepositoryWebhook>();
    public DbSet<Project> Project => Set<Project>();
    public DbSet<ProjectRepository> ProjectRepository => Set<ProjectRepository>();
    public DbSet<Role> Role => Set<Role>();
    public DbSet<Permission> Permission => Set<Permission>();
    public DbSet<RoleUser> RoleUser => Set<RoleUser>();
    public DbSet<RolePermission> RolePermission => Set<RolePermission>();
    public DbSet<UserPermission> UserPermission => Set<UserPermission>();
    public DbSet<OAuthPendingState> OAuthPendingState => Set<OAuthPendingState>();
    public DbSet<Workflow> Workflow => Set<Workflow>();
    public DbSet<WorkflowVersion> WorkflowVersion => Set<WorkflowVersion>();
    public DbSet<WorkflowActivation> WorkflowActivation => Set<WorkflowActivation>();
    public DbSet<WorkflowRun> WorkflowRun => Set<WorkflowRun>();
    public DbSet<WorkflowRunRequest> WorkflowRunRequest => Set<WorkflowRunRequest>();
    public DbSet<WorkflowRunRecord> WorkflowRunRecord => Set<WorkflowRunRecord>();
    public DbSet<WorkflowArtifact> WorkflowArtifact => Set<WorkflowArtifact>();
    public DbSet<WorkflowRunNode> WorkflowRunNode => Set<WorkflowRunNode>();
    public DbSet<WorkflowRunVariable> WorkflowRunVariable => Set<WorkflowRunVariable>();
    public DbSet<WorkflowRunMapInput> WorkflowRunMapInput => Set<WorkflowRunMapInput>();
    public DbSet<WorkflowRerunLease> WorkflowRerunLease => Set<WorkflowRerunLease>();
    public DbSet<WorkflowRunWait> WorkflowRunWait => Set<WorkflowRunWait>();
    public DbSet<WorkPlan> WorkPlan => Set<WorkPlan>();
    public DbSet<Variable> Variable => Set<Variable>();
    public DbSet<Conversation> Conversation => Set<Conversation>();
    public DbSet<ConversationMember> ConversationMember => Set<ConversationMember>();
    public DbSet<Message> Message => Set<Message>();
    public DbSet<MessageReference> MessageReference => Set<MessageReference>();
    public DbSet<UserProviderIdentity> UserProviderIdentity => Set<UserProviderIdentity>();
    public DbSet<AgentRun> AgentRun => Set<AgentRun>();
    public DbSet<AgentRunEvent> AgentRunEvent => Set<AgentRunEvent>();
    public DbSet<AgentDefinition> AgentDefinition => Set<AgentDefinition>();
    public DbSet<SkillDefinition> SkillDefinition => Set<SkillDefinition>();
    public DbSet<AgentSkillBinding> AgentSkillBinding => Set<AgentSkillBinding>();
    public DbSet<Pack> Pack => Set<Pack>();
    public DbSet<ModelCredential> ModelCredential => Set<ModelCredential>();
    public DbSet<ModelCredentialModel> ModelCredentialModel => Set<ModelCredentialModel>();
    public DbSet<ToolCallLedger> ToolCallLedger => Set<ToolCallLedger>();
    public DbSet<SupervisorDecisionRecord> SupervisorDecisionRecord => Set<SupervisorDecisionRecord>();
    public DbSet<SupervisorTapeSummaryRecord> SupervisorTapeSummaryRecord => Set<SupervisorTapeSummaryRecord>();
    public DbSet<WorkSession> WorkSession => Set<WorkSession>();
    public DbSet<PublishManifest> PublishManifest => Set<PublishManifest>();
    public DbSet<CompletionRequirement> CompletionRequirement => Set<CompletionRequirement>();
    public DbSet<CompletionReceipt> CompletionReceipt => Set<CompletionReceipt>();

    /// <summary>The shared ASP.NET Data Protection key-ring (<see cref="IDataProtectionKeyContext"/>) — persisted in Postgres so every API/worker pod decrypts the same credentials. Mapped to <c>data_protection_keys</c> below; the table is created by DbUp 0074.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(UserConfiguration).Assembly);

        // Explicit mapping (not left to the snake_case convention) so the framework key-store entity lines up
        // EXACTLY with the hand-written DbUp 0074 table — a deterministic contract, not a convention coincidence.
        modelBuilder.Entity<DataProtectionKey>(b =>
        {
            b.ToTable("data_protection_keys");
            b.HasKey(k => k.Id);
            b.Property(k => k.Id).HasColumnName("id");
            b.Property(k => k.FriendlyName).HasColumnName("friendly_name");
            b.Property(k => k.Xml).HasColumnName("xml");
        });

        // Global default: bot (non-human) users are invisible to EVERY User query. A request opts
        // back in by referencing the scoped IBotVisibility (flipped by BotVisibilityBehavior for
        // IBotInclusive requests) — so the exclusion can't be forgotten by a new query, only
        // explicitly waived. _botVisibility is an instance member, so EF re-evaluates it per query.
        modelBuilder.Entity<User>().HasQueryFilter(u => _botVisibility.IncludeBots || !u.IsBot);

        ApplyUtcDateTimeOffsetConverter(modelBuilder);
    }

    private static void ApplyUtcDateTimeOffsetConverter(ModelBuilder modelBuilder)
    {
        var converter = new ValueConverter<DateTimeOffset, DateTimeOffset>(v => v.ToUniversalTime(), v => v);
        var nullableConverter = new ValueConverter<DateTimeOffset?, DateTimeOffset?>(v => v.HasValue ? v.Value.ToUniversalTime() : null, v => v);

        foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(e => e.GetProperties()))
        {
            if (property.ClrType == typeof(DateTimeOffset)) property.SetValueConverter(converter);
            else if (property.ClrType == typeof(DateTimeOffset?)) property.SetValueConverter(nullableConverter);
        }
    }

    public override int SaveChanges()
    {
        ChangeTracker.DetectChanges();
        ApplyAuditFields();
        return base.SaveChanges();
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ChangeTracker.DetectChanges();
        ApplyAuditFields();
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken).ConfigureAwait(false);
    }

    private void ApplyAuditFields()
    {
        var now = DateTimeOffset.UtcNow;
        // Fallback to seeded system user so DbUp migrations and any code path without
        // ICurrentUser (e.g. background bootstrap, anonymous OAuth callback) still satisfy
        // NOT NULL audit columns. Handlers that have a more specific user (e.g. OAuth
        // callback using state.InitiatorUserId) may pre-set CreatedBy on the entity — that
        // value is honored if non-empty.
        var userId = _currentUser?.Id ?? SystemUsers.SeederId;

        foreach (var entry in ChangeTracker.Entries<IAuditable>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    if (entry.Entity.CreatedDate == default) entry.Entity.CreatedDate = now;
                    if (entry.Entity.CreatedBy == Guid.Empty) entry.Entity.CreatedBy = userId;
                    if (entry.Entity.LastModifiedDate == default) entry.Entity.LastModifiedDate = now;
                    if (entry.Entity.LastModifiedBy == Guid.Empty) entry.Entity.LastModifiedBy = userId;
                    break;

                case EntityState.Modified:
                    entry.Entity.LastModifiedDate = now;
                    entry.Entity.LastModifiedBy = userId;
                    entry.Property(nameof(IAuditable.CreatedDate)).IsModified = false;
                    entry.Property(nameof(IAuditable.CreatedBy)).IsModified = false;
                    break;
            }
        }
    }
}
