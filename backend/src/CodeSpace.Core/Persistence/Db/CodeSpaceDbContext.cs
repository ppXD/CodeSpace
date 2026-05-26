using CodeSpace.Messages.Constants;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Persistence.EntityConfigurations;
using CodeSpace.Core.Services.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CodeSpace.Core.Persistence.Db;

public class CodeSpaceDbContext : DbContext, IUnitOfWork
{
    private readonly ICurrentUser? _currentUser;

    public CodeSpaceDbContext(DbContextOptions<CodeSpaceDbContext> options, ICurrentUser? currentUser = null) : base(options)
    {
        _currentUser = currentUser;
    }

    public DbSet<User> User => Set<User>();
    public DbSet<Team> Team => Set<Team>();
    public DbSet<TeamMembership> TeamMembership => Set<TeamMembership>();
    public DbSet<ProviderInstance> ProviderInstance => Set<ProviderInstance>();
    public DbSet<Credential> Credential => Set<Credential>();
    public DbSet<Repository> Repository => Set<Repository>();
    public DbSet<RepositoryWebhook> RepositoryWebhook => Set<RepositoryWebhook>();
    public DbSet<OutboxMessage> OutboxMessage => Set<OutboxMessage>();
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
    public DbSet<Variable> Variable => Set<Variable>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(UserConfiguration).Assembly);

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
