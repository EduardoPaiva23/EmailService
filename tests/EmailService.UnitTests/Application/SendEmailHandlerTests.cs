using System.Text.Json;
using EmailService.Application.Exceptions;
using EmailService.Application.Messaging;
using EmailService.Application.UseCases.SendEmail;
using EmailService.Domain.Entities;
using EmailService.Domain.Enums;
using Microsoft.Extensions.Time.Testing;

namespace EmailService.UnitTests.Application;

public class SendEmailHandlerTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly SendEmailOptions Options = new() { MaxAttempts = 3, IdempotencyKeyTtl = TimeSpan.FromHours(24) };

    private readonly InMemoryStore _store = new();
    private readonly FakeTimeProvider _clock = new(Start);
    private readonly Guid _clientId = Guid.NewGuid();

    private SendEmailHandler Handler() => new(_store, _store, _store, _store, _clock, Options);

    private SendEmailCommand Command(string key = "req-1", string subject = "Assunto") =>
        new(_clientId, key, Guid.NewGuid(), "destino@example.com", subject, "<p>Olá</p>", true);

    [Fact]
    public async Task NewRequest_CreatesPendingEmailOutboxAndKeyInOneSave()
    {
        var command = Command();

        var result = await Handler().HandleAsync(command);

        Assert.False(result.Replayed);
        Assert.Equal(EmailStatus.Pending, result.Status);
        Assert.Equal(Start.UtcDateTime, result.CreatedAt);
        Assert.Equal(1, _store.SaveCount);

        var email = Assert.Single(_store.Emails);
        Assert.Equal(result.Id, email.Id);
        Assert.Equal(_clientId, email.ClientId);
        Assert.Equal(command.CorrelationId, email.CorrelationId);
        Assert.Equal(3, email.MaxAttempts);

        var key = Assert.Single(_store.Keys);
        Assert.Equal(email.Id, key.EmailId);
        Assert.Equal(Start.UtcDateTime.AddHours(24), key.ExpiresAt);
    }

    [Fact]
    public async Task NewRequest_OutboxCarriesSendEmailMessageAvailableNow()
    {
        var command = Command();

        var result = await Handler().HandleAsync(command);

        var outbox = Assert.Single(_store.Outbox);
        Assert.Equal(SendEmailMessage.Type, outbox.Type);
        Assert.Equal(Start.UtcDateTime, outbox.AvailableAt);
        var message = JsonSerializer.Deserialize<SendEmailMessage>(outbox.Payload, SendEmailMessage.JsonOptions);
        Assert.Equal(new SendEmailMessage(result.Id, command.CorrelationId, Start.UtcDateTime), message);
    }

    [Fact]
    public async Task SameKeyAndPayload_ReturnsOriginalEmailWithoutCreatingAnother()
    {
        var first = await Handler().HandleAsync(Command());
        _clock.Advance(TimeSpan.FromMinutes(5));

        var replay = await Handler().HandleAsync(Command());

        Assert.True(replay.Replayed);
        Assert.Equal(first.Id, replay.Id);
        Assert.Equal(first.CreatedAt, replay.CreatedAt);
        Assert.Single(_store.Emails);
        Assert.Single(_store.Outbox);
        Assert.Equal(1, _store.SaveCount);
    }

    [Fact]
    public async Task SameKeyWithDifferentPayload_ThrowsIdempotencyConflict()
    {
        await Handler().HandleAsync(Command(subject: "Original"));

        await Assert.ThrowsAsync<IdempotencyConflictException>(
            () => Handler().HandleAsync(Command(subject: "Outro assunto")));

        Assert.Single(_store.Emails);
    }

    [Fact]
    public async Task SameKeyFromAnotherClient_IsIndependent()
    {
        await Handler().HandleAsync(Command());
        var otherClient = Command() with { ClientId = Guid.NewGuid() };

        var result = await Handler().HandleAsync(otherClient);

        Assert.False(result.Replayed);
        Assert.Equal(2, _store.Emails.Count);
    }

    [Fact]
    public async Task ExpiredKey_IsReplacedAndRequestTreatedAsNew()
    {
        var first = await Handler().HandleAsync(Command());
        _clock.Advance(TimeSpan.FromHours(25));

        var second = await Handler().HandleAsync(Command(subject: "Payload novo"));

        Assert.False(second.Replayed);
        Assert.NotEqual(first.Id, second.Id);
        var key = Assert.Single(_store.Keys);
        Assert.Equal(second.Id, key.EmailId);
    }

    [Fact]
    public async Task ConcurrentRequestWithSameKey_ResolvesToReplay()
    {
        // Outra requisição idêntica grava a chave entre a verificação e o save.
        var command = Command();
        Email? winner = null;
        _store.BeforeNextSave = () =>
        {
            var now = Start.UtcDateTime;
            winner = Email.Create(_clientId, Guid.NewGuid(), command.To, command.Subject, command.Body, command.IsHtml, 3, now);
            _store.Emails.Add(winner);
            _store.Keys.Add(IdempotencyKey.Create(_clientId, command.IdempotencyKey,
                SendEmailRequestHash.Compute(command), winner.Id, now, TimeSpan.FromHours(24)));
        };

        var result = await Handler().HandleAsync(command);

        Assert.True(result.Replayed);
        Assert.Equal(winner!.Id, result.Id);
        Assert.Single(_store.Emails);
    }

    [Fact]
    public async Task ConcurrentRequestWithSameKeyButDifferentPayload_ThrowsIdempotencyConflict()
    {
        var command = Command();
        _store.BeforeNextSave = () =>
        {
            var now = Start.UtcDateTime;
            var other = Email.Create(_clientId, Guid.NewGuid(), command.To, "Outro", command.Body, command.IsHtml, 3, now);
            _store.Emails.Add(other);
            _store.Keys.Add(IdempotencyKey.Create(_clientId, command.IdempotencyKey, "hash-diferente", other.Id, now, TimeSpan.FromHours(24)));
        };

        await Assert.ThrowsAsync<IdempotencyConflictException>(() => Handler().HandleAsync(command));
    }

    [Fact]
    public void RequestHash_DependsOnPayloadOnly()
    {
        var a = Command();
        var sameContentOtherCorrelation = a with { CorrelationId = Guid.NewGuid() };

        Assert.Equal(SendEmailRequestHash.Compute(a), SendEmailRequestHash.Compute(sameContentOtherCorrelation));
        Assert.NotEqual(SendEmailRequestHash.Compute(a), SendEmailRequestHash.Compute(a with { IsHtml = false }));
        Assert.NotEqual(SendEmailRequestHash.Compute(a), SendEmailRequestHash.Compute(a with { To = "outro@example.com" }));
        Assert.Equal(64, SendEmailRequestHash.Compute(a).Length);
    }
}
