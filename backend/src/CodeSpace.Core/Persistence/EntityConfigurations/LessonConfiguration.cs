using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CodeSpace.Core.Persistence.EntityConfigurations;

public class LessonConfiguration : IEntityTypeConfiguration<Lesson>
{
    public void Configure(EntityTypeBuilder<Lesson> builder)
    {
        builder.ToTable("lesson");
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Mode).IsRequired();
        builder.Property(l => l.FailureClass).IsRequired();
        builder.Property(l => l.WhatFailed).IsRequired();
        builder.Property(l => l.Why).IsRequired();
        builder.Property(l => l.HowToApply).IsRequired();
        builder.Property(l => l.SourceRunIds).IsRequired();
        builder.Property(l => l.DistilledByModel).IsRequired();
        builder.HasIndex(l => new { l.TeamId, l.Mode });
    }
}
