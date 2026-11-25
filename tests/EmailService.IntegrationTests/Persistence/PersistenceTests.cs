using EmailService.Application.Abstractions.Persistence;
using EmailService.Application.Exceptions;
using EmailService.Domain.Entities;
using EmailService.Domain.Enums;
using EmailService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EmailService.IntegrationTests.Persistence;

[Collection(SqlServerCollection.Name)]
public class PersistenceTests(SqlServerFixture fixture)
{
    private static readonly TimeSpan IdempotencyTtl = TimeSpan.FromHours(24);

    private static (Email Email, OutboxMessage Outbox, IdempotencyKey Key) NewRequest(Guid clientId, string key)
    {
        var now = DateTime.UtcNow;
        var email = Email.Create(clientId, Guid.NewGuid(), "destino@example.com", "Assunto", "<p>Olá</p>", true, 5, now);
        var outbox = OutboxMessage.Create("SendEmail", $"{{\"emailId\":\"{email.Id}\"}}", now, now);
        var idempotencyKey = IdempotencyKey.Create(clientId, key, "hash-" + key, email.Id, now, IdempotencyTtl);
        return (email, outbox, idempotencyKey);
    }

    private static async Task SaveRequestAsync(IServiceProvider services, (Email Email, OutboxMessage Outbox, IdempotencyKey Key) request)
    {
        services.GetRequiredService<IEmailRepository>().Add(request.Email);
        services.GetRequiredService<IOutboxRepository>().Add(request.Outbox);
        services.GetRequiredService<IIdempotencyKeyRepository>().Add(request.Key);
        await services.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
    }

    [Fact]
    public async Task EmailOutboxAndIdempotencyKey_AreSavedTogether()
    {
        var client = await fixture.CreateClientAsync();
        var request = NewRequest(client.Id, "req-1");

        await using (var scope = fixture.CreateScope())
            await SaveRequestAsync(scope.ServiceProvider, request);

        await using var verify = fixture.CreateScope();
        var emails = verify.ServiceProvider.GetRequiredService<IEmailRepository>();
        var keys = verify.ServiceProvider.GetRequiredService<IIdempotencyKeyRepository>();
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();

        var email = await emails.GetByIdAsync(request.Email.Id);
        Assert.NotNull(email);
        Assert.Equal(EmailStatus.Pending, email.Status);
        Assert.Equal("<p>Olá</p>", email.Body);
        Assert.Equal(DateTimeKind.Utc, email.CreatedAt.Kind);
        Assert.NotEmpty(email.RowVersion);

        var key = await keys.GetAsync(client.Id, "req-1");
        Assert.NotNull(key);
        Assert.Equal(request.Email.Id, key.EmailId);

        var outbox = await db.OutboxMessages.SingleAsync(o => o.Id == request.Outbox.Id);
        Assert.Null(outbox.PublishedAt);
    }

    [Fact]
    public async Task WhenOneWriteFails_NothingIsPersisted()
    {
        var client = await fixture.CreateClientAsync();

        await using (var scope = fixture.CreateScope())
            await SaveRequestAsync(scope.ServiceProvider, NewRequest(client.Id, "req-duplicada"));

        // Segunda solicitação com a mesma chave: a PK (ClientId, Key) é violada.
        var second = NewRequest(client.Id, "req-duplicada");
        await using (var scope = fixture.CreateScope())
            await Assert.ThrowsAnyAsync<Exception>(() => SaveRequestAsync(scope.ServiceProvider, second));

        await using var verify = fixture.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Emails.AnyAsync(e => e.Id == second.Email.Id));
        Assert.False(await db.OutboxMessages.AnyAsync(o => o.Id == second.Outbox.Id));
    }

    [Fact]
    public async Task ConcurrentTransition_IsBlockedByRowVersion()
    {
        var client = await fixture.CreateClientAsync();
        var request = NewRequest(client.Id, "req-concorrencia");
        await using (var scope = fixture.CreateScope())
            await SaveRequestAsync(scope.ServiceProvider, request);

        await using var workerA = fixture.CreateScope();
        await using var workerB = fixture.CreateScope();
        var emailA = await workerA.ServiceProvider.GetRequiredService<IEmailRepository>().GetByIdAsync(request.Email.Id);
        var emailB = await workerB.ServiceProvider.GetRequiredService<IEmailRepository>().GetByIdAsync(request.Email.Id);

        var now = DateTime.UtcNow;
        emailA!.StartProcessing(now, now.AddMinutes(2));
        await workerA.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();

        // B leu o e-mail ainda em Pending, mas a versão no banco mudou.
        emailB!.StartProcessing(now, now.AddMinutes(5));
        await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => workerB.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync());

        await using var verify = fixture.CreateScope();
        var stored = await verify.ServiceProvider.GetRequiredService<IEmailRepository>().GetByIdAsync(request.Email.Id);
        Assert.Equal(EmailStatus.Processing, stored!.Status);
        Assert.Equal(now.AddMinutes(2), stored.LockedUntil!.Value, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task EmailAttempt_IsPersistedWithOutcome()
    {
        var client = await fixture.CreateClientAsync();
        var request = NewRequest(client.Id, "req-tentativa");
        await using (var scope = fixture.CreateScope())
            await SaveRequestAsync(scope.ServiceProvider, request);

        var started = DateTime.UtcNow;
        var attempt = EmailAttempt.Start(request.Email.Id, 1, started, "worker-1");
        attempt.Finish(started.AddMilliseconds(800), AttemptOutcome.TransientError, 421, "try again later");

        await using (var scope = fixture.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IEmailRepository>().AddAttempt(attempt);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        await using var verify = fixture.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.EmailAttempts.SingleAsync(a => a.EmailId == request.Email.Id);
        Assert.Equal(AttemptOutcome.TransientError, stored.Outcome);
        Assert.Equal(421, stored.SmtpStatusCode);
        Assert.Equal(800, stored.DurationMs);
    }

    [Theory]
    [InlineData("OutboxMessages", "IX_OutboxMessages_AvailableAt", "([PublishedAt] IS NULL)", false)]
    [InlineData("Emails", "IX_Emails_ClientId_CreatedAt", null, false)]
    [InlineData("Emails", "IX_Emails_LockedUntil", "([Status]=(2))", false)]
    [InlineData("EmailAttempts", "IX_EmailAttempts_EmailId", null, false)]
    [InlineData("IdempotencyKeys", "IX_IdempotencyKeys_ExpiresAt", null, false)]
    [InlineData("ApiClients", "IX_ApiClients_KeyPrefix", null, true)]
    public async Task Migration_CreatesPlannedIndexes(string table, string index, string? filter, bool unique)
    {
        await using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var found = await db.Database
            .SqlQuery<IndexInfo>($"""
                SELECT i.is_unique AS IsUnique, i.filter_definition AS FilterDefinition
                FROM sys.indexes i
                WHERE i.object_id = OBJECT_ID({table}) AND i.name = {index}
                """)
            .SingleOrDefaultAsync();

        Assert.NotNull(found);
        Assert.Equal(unique, found.IsUnique);
        Assert.Equal(filter, found.FilterDefinition);
    }

    [Fact]
    public async Task Emails_ClientCreatedAtIndex_IsDescendingOnCreatedAt()
    {
        await using var scope = fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var descending = await db.Database
            .SqlQuery<bool>($"""
                SELECT ic.is_descending_key AS Value
                FROM sys.indexes i
                JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE i.object_id = OBJECT_ID('Emails') AND i.name = 'IX_Emails_ClientId_CreatedAt' AND c.name = 'CreatedAt'
                """)
            .SingleAsync();

        Assert.True(descending);
    }

    public sealed record IndexInfo(bool IsUnique, string? FilterDefinition);
}
