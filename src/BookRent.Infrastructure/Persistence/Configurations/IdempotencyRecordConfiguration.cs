using BookRent.Domain.Idempotency;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BookRent.Infrastructure.Persistence.Configurations;

internal sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("idempotency_records", BookRentDbContext.Schema);

        // Chave composta (endpoint, key): a chave e escopada ao endpoint, entao dois
        // endpoints podem receber a mesma string de um cliente ingenuo sem colidir.
        // E este indice unico que serve de mutex — a segunda requisicao com a mesma
        // chave bloqueia nele ate a primeira commitar. Ver secao 3 do plano.
        builder.HasKey(record => new { record.Endpoint, record.Key });

        builder.Property(record => record.Endpoint)
            .HasColumnName("endpoint")
            .HasMaxLength(IdempotencyRecord.MaxEndpointLength);

        builder.Property(record => record.Key)
            .HasColumnName("key")
            .HasMaxLength(IdempotencyRecord.MaxKeyLength);

        builder.Property(record => record.RequestHash)
            .HasColumnName("request_hash")
            .HasMaxLength(IdempotencyRecord.RequestHashLength)
            .IsFixedLength()
            .IsRequired();

        builder.Property(record => record.ResponseStatus).HasColumnName("response_status");

        builder.Property(record => record.ResponseBody)
            .HasColumnName("response_body")
            .HasColumnType("jsonb");

        // Ponteiro de diagnostico, sem FK: o emprestimo nasce no mesmo commit, e uma
        // restricao aqui so atrapalharia o expurgo das chaves expiradas.
        builder.Property(record => record.LoanId).HasColumnName("loan_id");

        builder.Property(record => record.CreatedAt).HasColumnName("created_at");
        builder.Property(record => record.ExpiresAt).HasColumnName("expires_at");

        builder.HasIndex(record => record.ExpiresAt)
            .HasDatabaseName("ix_idempotency_records_expires_at");
    }
}
