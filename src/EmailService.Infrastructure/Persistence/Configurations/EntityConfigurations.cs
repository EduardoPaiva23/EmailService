using EmailService.Application.Security;
using EmailService.Domain.Entities;
using EmailService.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EmailService.Infrastructure.Persistence.Configurations;

internal sealed class EmailConfiguration : IEntityTypeConfiguration<Email>
{
    public void Configure(EntityTypeBuilder<Email> builder)
    {
        builder.ToTable("Emails");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();

        builder.Property(e => e.Recipient).HasMaxLength(320).IsRequired();
        builder.Property(e => e.Subject).HasMaxLength(998).IsRequired();
        builder.Property(e => e.Body).IsRequired(); // nvarchar(max)
        builder.Property(e => e.Status).HasConversion<int>();
        builder.Property(e => e.LastError);
        builder.Property(e => e.ProviderMessageId).HasMaxLength(256);
        builder.Property(e => e.RowVersion).IsRowVersion();

        builder.HasOne<ApiClient>().WithMany().HasForeignKey(e => e.ClientId).OnDelete(DeleteBehavior.Restrict);

        // Histórico por cliente.
        builder.HasIndex(e => new { e.ClientId, e.CreatedAt })
            .IsDescending(false, true)
            .HasDatabaseName("IX_Emails_ClientId_CreatedAt");

        // Job de recuperação de Processing abandonado.
        builder.HasIndex(e => e.LockedUntil)
            .HasFilter($"[Status] = {(int)EmailStatus.Processing}")
            .HasDatabaseName("IX_Emails_LockedUntil");

        builder.Ignore(e => e.HasAttemptsRemaining);
    }
}

internal sealed class EmailAttemptConfiguration : IEntityTypeConfiguration<EmailAttempt>
{
    public void Configure(EntityTypeBuilder<EmailAttempt> builder)
    {
        builder.ToTable("EmailAttempts");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();

        builder.Property(a => a.Outcome).HasConversion<int?>();
        builder.Property(a => a.ErrorMessage);
        builder.Property(a => a.WorkerId).HasMaxLength(100).IsRequired();

        builder.HasOne<Email>().WithMany().HasForeignKey(a => a.EmailId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(a => a.EmailId).HasDatabaseName("IX_EmailAttempts_EmailId");
    }
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("OutboxMessages");
        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).ValueGeneratedNever();

        builder.Property(o => o.Type).HasMaxLength(100).IsRequired();
        builder.Property(o => o.Payload).IsRequired(); // nvarchar(max)
        builder.Property(o => o.LastError);
        builder.Property(o => o.LockedBy).HasMaxLength(100);

        // Busca do Dispatcher: apenas eventos pendentes.
        builder.HasIndex(o => o.AvailableAt)
            .HasFilter("[PublishedAt] IS NULL")
            .HasDatabaseName("IX_OutboxMessages_AvailableAt");
    }
}

internal sealed class IdempotencyKeyConfiguration : IEntityTypeConfiguration<IdempotencyKey>
{
    public void Configure(EntityTypeBuilder<IdempotencyKey> builder)
    {
        builder.ToTable("IdempotencyKeys");
        builder.HasKey(k => new { k.ClientId, k.Key });

        builder.Property(k => k.Key).HasMaxLength(100);
        builder.Property(k => k.RequestHash).HasMaxLength(64).IsUnicode(false).IsRequired();

        builder.HasOne<ApiClient>().WithMany().HasForeignKey(k => k.ClientId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Email>().WithMany().HasForeignKey(k => k.EmailId).OnDelete(DeleteBehavior.Cascade);

        // Limpeza de chaves expiradas.
        builder.HasIndex(k => k.ExpiresAt).HasDatabaseName("IX_IdempotencyKeys_ExpiresAt");
    }
}

internal sealed class ApiClientConfiguration : IEntityTypeConfiguration<ApiClient>
{
    public void Configure(EntityTypeBuilder<ApiClient> builder)
    {
        builder.ToTable("ApiClients");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();

        builder.Property(c => c.Name).HasMaxLength(200).IsRequired();
        builder.Property(c => c.KeyPrefix).HasMaxLength(ApiKeyHasher.PrefixLength).IsUnicode(false).IsRequired();
        builder.Property(c => c.KeyHash).HasMaxLength(64).IsUnicode(false).IsRequired();

        builder.HasIndex(c => c.KeyPrefix).IsUnique().HasDatabaseName("IX_ApiClients_KeyPrefix");
    }
}
