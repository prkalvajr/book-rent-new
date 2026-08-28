using BookRent.Domain.Auditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BookRent.Infrastructure.Persistence.Configurations;

internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("audit_events", BookRentDbContext.Schema);

        builder.HasKey(auditEvent => auditEvent.Id);

        // bigint identity: o id nunca vai em URL nem e chave estrangeira, entao os 16
        // bytes de um Guid seriam custo sem contrapartida na tabela que mais cresce.
        builder.Property(auditEvent => auditEvent.Id)
            .HasColumnName("id")
            .UseIdentityByDefaultColumn();

        builder.Property(auditEvent => auditEvent.EntityType)
            .HasColumnName("entity_type")
            .HasMaxLength(AuditEvent.MaxEntityTypeLength)
            .IsRequired();

        builder.Property(auditEvent => auditEvent.EntityId).HasColumnName("entity_id");

        builder.Property(auditEvent => auditEvent.Action)
            .HasColumnName("action")
            .HasMaxLength(AuditEvent.MaxActionLength)
            .IsRequired();

        builder.Property(auditEvent => auditEvent.Actor)
            .HasColumnName("actor")
            .HasMaxLength(AuditEvent.MaxActorLength)
            .IsRequired();

        builder.Property(auditEvent => auditEvent.OccurredAt).HasColumnName("occurred_at");

        builder.Property(auditEvent => auditEvent.CorrelationId)
            .HasColumnName("correlation_id")
            .HasMaxLength(AuditEvent.MaxCorrelationIdLength)
            .IsRequired();

        // jsonb: permite consultar dentro do payload sem tratar a coluna como texto opaco.
        builder.Property(auditEvent => auditEvent.Data)
            .HasColumnName("data")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.HasIndex(auditEvent => new { auditEvent.EntityType, auditEvent.EntityId, auditEvent.OccurredAt })
            .IsDescending(false, false, true)
            .HasDatabaseName("ix_audit_events_entity");

        builder.HasIndex(auditEvent => auditEvent.OccurredAt)
            .IsDescending(true)
            .HasDatabaseName("ix_audit_events_occurred_at");
    }
}
