using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

/// <summary>
/// EF configuration for <see cref="WorkSession"/>. Most columns map by the global
/// <c>UseSnakeCaseNamingConvention()</c>; only the FK, the two enum-as-string conversions, the
/// string lengths, and the jsonb column need spelling out. Keep the team-listing index in sync with
/// migration <c>0069_work_session.sql</c> — both describe the same physical schema.
/// </summary>
public class WorkSessionConfiguration : IEntityTypeConfiguration<WorkSession>
{
    public void Configure(EntityTypeBuilder<WorkSession> builder)
    {
        builder.ToTable("work_session");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Kind).HasConversion<string>().HasMaxLength(16);
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(s => s.Title).HasMaxLength(WorkSession.TitleMaxLength);
        builder.Property(s => s.ScopeJson).HasColumnName("scope_jsonb").HasColumnType("jsonb");

        // The MRU sort key carries a store DEFAULT now() (migration 0089) — so any writer that doesn't set it (the
        // service open path does) still lands a real instant, never 0001-01-01. EF sends an explicit value when set.
        builder.Property(s => s.LastActivityAt).HasDefaultValueSql("now()");

        builder.HasOne(s => s.Team).WithMany().HasForeignKey(s => s.TeamId);

        // "List a team's sessions, newest first" — the session-index access path. Created_date DESC
        // serves both the all-sessions and open-only views (Status is a cheap recheck-tier filter).
        builder.HasIndex(s => new { s.TeamId, s.CreatedDate });

        // "List a team's sessions, most-recently-active first" — the MRU sessions-index keyset path (the read API's
        // ListAsync). Mirror migration 0089_work_session_last_activity.sql (last_activity_at DESC, id DESC).
        builder.HasIndex(s => new { s.TeamId, s.LastActivityAt });
    }
}
