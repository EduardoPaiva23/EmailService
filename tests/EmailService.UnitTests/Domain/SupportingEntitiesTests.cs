using EmailService.Domain.Entities;
using EmailService.Domain.Enums;
using EmailService.Domain.Exceptions;

namespace EmailService.UnitTests.Domain;

public class SupportingEntitiesTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    // ---------- EmailAttempt ----------

    [Fact]
    public void EmailAttempt_Start_IsOpenUntilFinished()
    {
        var emailId = Guid.NewGuid();

        var attempt = EmailAttempt.Start(emailId, attemptNumber: 1, startedAt: Now, workerId: "worker-1");

        Assert.Equal(emailId, attempt.EmailId);
        Assert.Equal(1, attempt.AttemptNumber);
        Assert.Equal("worker-1", attempt.WorkerId);
        Assert.Null(attempt.FinishedAt);
        Assert.Null(attempt.Outcome);
    }

    [Fact]
    public void EmailAttempt_Finish_RecordsOutcomeAndDuration()
    {
        var attempt = EmailAttempt.Start(Guid.NewGuid(), 1, Now, "worker-1");

        attempt.Finish(Now.AddMilliseconds(1250), AttemptOutcome.PermanentError, smtpStatusCode: 550, errorMessage: "mailbox unavailable");

        Assert.Equal(Now.AddMilliseconds(1250), attempt.FinishedAt);
        Assert.Equal(1250, attempt.DurationMs);
        Assert.Equal(AttemptOutcome.PermanentError, attempt.Outcome);
        Assert.Equal(550, attempt.SmtpStatusCode);
        Assert.Equal("mailbox unavailable", attempt.ErrorMessage);
    }

    [Fact]
    public void EmailAttempt_FinishTwice_Throws()
    {
        var attempt = EmailAttempt.Start(Guid.NewGuid(), 1, Now, "worker-1");
        attempt.Finish(Now.AddSeconds(1), AttemptOutcome.Success);

        Assert.Throws<DomainException>(() => attempt.Finish(Now.AddSeconds(2), AttemptOutcome.Success));
    }

    [Fact]
    public void EmailAttempt_FinishBeforeStart_Throws()
    {
        var attempt = EmailAttempt.Start(Guid.NewGuid(), 1, Now, "worker-1");

        Assert.Throws<DomainException>(() => attempt.Finish(Now.AddSeconds(-1), AttemptOutcome.Success));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void EmailAttempt_RejectsAttemptNumberBelowOne(int attemptNumber)
    {
        Assert.Throws<DomainException>(() => EmailAttempt.Start(Guid.NewGuid(), attemptNumber, Now, "w"));
    }

    // ---------- OutboxMessage ----------

    [Fact]
    public void OutboxMessage_Create_IsPendingAndAvailableAtGivenTime()
    {
        var available = Now.AddMinutes(5);

        var message = OutboxMessage.Create("SendEmail", "{\"emailId\":\"x\"}", occurredAt: Now, availableAt: available);

        Assert.NotEqual(Guid.Empty, message.Id);
        Assert.Equal("SendEmail", message.Type);
        Assert.Equal(Now, message.OccurredAt);
        Assert.Equal(available, message.AvailableAt);
        Assert.Null(message.PublishedAt);
        Assert.Equal(0, message.PublishAttempts);
        Assert.Null(message.LockedUntil);
        Assert.Null(message.LockedBy);
    }

    [Fact]
    public void OutboxMessage_RejectsAvailableAtBeforeOccurredAt()
    {
        Assert.Throws<DomainException>(() => OutboxMessage.Create("SendEmail", "{}", Now, Now.AddSeconds(-1)));
    }

    [Theory]
    [InlineData("", "{}")]
    [InlineData("SendEmail", "")]
    public void OutboxMessage_RejectsMissingTypeOrPayload(string type, string payload)
    {
        Assert.Throws<DomainException>(() => OutboxMessage.Create(type, payload, Now, Now));
    }

    // ---------- IdempotencyKey ----------

    [Fact]
    public void IdempotencyKey_Create_ExpiresAfterTtl()
    {
        var clientId = Guid.NewGuid();
        var emailId = Guid.NewGuid();

        var key = IdempotencyKey.Create(clientId, "req-123", "hash", emailId, Now, TimeSpan.FromHours(24));

        Assert.Equal(clientId, key.ClientId);
        Assert.Equal("req-123", key.Key);
        Assert.Equal("hash", key.RequestHash);
        Assert.Equal(emailId, key.EmailId);
        Assert.Equal(Now, key.CreatedAt);
        Assert.Equal(Now.AddHours(24), key.ExpiresAt);
    }

    [Fact]
    public void IdempotencyKey_RejectsNonPositiveTtl()
    {
        Assert.Throws<DomainException>(() =>
            IdempotencyKey.Create(Guid.NewGuid(), "k", "h", Guid.NewGuid(), Now, TimeSpan.Zero));
    }

    [Fact]
    public void IdempotencyKey_MatchesOnlySameRequestHash()
    {
        var key = IdempotencyKey.Create(Guid.NewGuid(), "k", "hash-a", Guid.NewGuid(), Now, TimeSpan.FromHours(1));

        Assert.True(key.Matches("hash-a"));
        Assert.False(key.Matches("hash-b"));
    }

    // ---------- ApiClient ----------

    [Fact]
    public void ApiClient_Create_IsActive()
    {
        var client = ApiClient.Create("Sistema X", "es_dev_ab", "a1b2c3", Now);

        Assert.NotEqual(Guid.Empty, client.Id);
        Assert.Equal("Sistema X", client.Name);
        Assert.Equal("es_dev_ab", client.KeyPrefix);
        Assert.Equal("a1b2c3", client.KeyHash);
        Assert.True(client.IsActive);
        Assert.Equal(Now, client.CreatedAt);
    }

    [Fact]
    public void ApiClient_Deactivate_SetsInactive()
    {
        var client = ApiClient.Create("Sistema X", "es_dev_ab", "a1b2c3", Now);

        client.Deactivate();

        Assert.False(client.IsActive);
    }

    [Theory]
    [InlineData("", "p", "h")]
    [InlineData("n", "", "h")]
    [InlineData("n", "p", "")]
    public void ApiClient_RejectsMissingFields(string name, string prefix, string hash)
    {
        Assert.Throws<DomainException>(() => ApiClient.Create(name, prefix, hash, Now));
    }
}
