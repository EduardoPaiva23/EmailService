using EmailService.Domain.Entities;
using EmailService.Domain.Enums;
using EmailService.Domain.Exceptions;

namespace EmailService.UnitTests.Domain;

public class EmailTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static Email NewEmail(int maxAttempts = 5) =>
        Email.Create(
            clientId: Guid.NewGuid(),
            correlationId: Guid.NewGuid(),
            recipient: "destino@example.com",
            subject: "Assunto",
            body: "<p>Olá</p>",
            isHtml: true,
            maxAttempts: maxAttempts,
            now: Now);

    private static Email ProcessingEmail(int maxAttempts = 5)
    {
        var email = NewEmail(maxAttempts);
        email.StartProcessing(Now, Now.AddMinutes(2));
        return email;
    }

    private static Email FailedEmail()
    {
        var email = ProcessingEmail();
        email.MarkFailed(Now, "550 mailbox unavailable");
        return email;
    }

    // ---------- Create ----------

    [Fact]
    public void Create_StartsPendingWithNoAttempts()
    {
        var email = NewEmail();

        Assert.NotEqual(Guid.Empty, email.Id);
        Assert.Equal(EmailStatus.Pending, email.Status);
        Assert.Equal(0, email.Attempts);
        Assert.Equal(5, email.MaxAttempts);
        Assert.Equal(Now, email.CreatedAt);
        Assert.Equal(Now, email.UpdatedAt);
        Assert.Null(email.SentAt);
        Assert.Null(email.LockedUntil);
    }

    [Theory]
    [InlineData("", "Assunto", "Corpo")]
    [InlineData("  ", "Assunto", "Corpo")]
    [InlineData("a@example.com", "", "Corpo")]
    [InlineData("a@example.com", "Assunto", "")]
    public void Create_RejectsMissingRequiredText(string recipient, string subject, string body)
    {
        Assert.Throws<DomainException>(() => Email.Create(
            Guid.NewGuid(), Guid.NewGuid(), recipient, subject, body, false, 5, Now));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_RejectsMaxAttemptsBelowOne(int maxAttempts)
    {
        Assert.Throws<DomainException>(() => NewEmail(maxAttempts));
    }

    // ---------- StartProcessing: Pending -> Processing ----------

    [Fact]
    public void StartProcessing_FromPending_SetsProcessingAndLease()
    {
        var email = NewEmail();
        var later = Now.AddSeconds(10);

        email.StartProcessing(later, later.AddMinutes(2));

        Assert.Equal(EmailStatus.Processing, email.Status);
        Assert.Equal(later.AddMinutes(2), email.LockedUntil);
        Assert.Equal(later, email.UpdatedAt);
    }

    [Fact]
    public void StartProcessing_RejectsLeaseNotInTheFuture()
    {
        var email = NewEmail();

        Assert.Throws<DomainException>(() => email.StartProcessing(Now, Now));
    }

    // ---------- MarkSent: Processing -> Sent ----------

    [Fact]
    public void MarkSent_FromProcessing_RecordsSendAndReleasesLease()
    {
        var email = ProcessingEmail();
        var later = Now.AddSeconds(3);

        email.MarkSent(later, "provider-123");

        Assert.Equal(EmailStatus.Sent, email.Status);
        Assert.Equal(later, email.SentAt);
        Assert.Equal("provider-123", email.ProviderMessageId);
        Assert.Equal(1, email.Attempts);
        Assert.Null(email.LockedUntil);
        Assert.Null(email.NextAttemptAt);
    }

    // ---------- ScheduleRetry: Processing -> Pending ----------

    [Fact]
    public void ScheduleRetry_FromProcessing_ReturnsToPendingWithNextAttempt()
    {
        var email = ProcessingEmail();
        var next = Now.AddMinutes(1);

        email.ScheduleRetry(Now, next, "421 try again later");

        Assert.Equal(EmailStatus.Pending, email.Status);
        Assert.Equal(1, email.Attempts);
        Assert.Equal(next, email.NextAttemptAt);
        Assert.Equal("421 try again later", email.LastError);
        Assert.Null(email.LockedUntil);
    }

    [Fact]
    public void ScheduleRetry_WhenNoAttemptsRemain_Throws()
    {
        var email = ProcessingEmail(maxAttempts: 1);

        Assert.Throws<DomainException>(() => email.ScheduleRetry(Now, Now.AddMinutes(1), "erro"));
        Assert.Equal(EmailStatus.Processing, email.Status);
        Assert.Equal(0, email.Attempts);
    }

    [Fact]
    public void ScheduleRetry_RejectsNextAttemptInThePast()
    {
        var email = ProcessingEmail();

        Assert.Throws<DomainException>(() => email.ScheduleRetry(Now, Now.AddSeconds(-1), "erro"));
    }

    [Fact]
    public void HasAttemptsRemaining_IsFalseOnLastAttempt()
    {
        Assert.True(ProcessingEmail(maxAttempts: 2).HasAttemptsRemaining);
        Assert.False(ProcessingEmail(maxAttempts: 1).HasAttemptsRemaining);
    }

    // ---------- MarkFailed: Processing -> Failed ----------

    [Fact]
    public void MarkFailed_FromProcessing_RecordsErrorAndReleasesLease()
    {
        var email = ProcessingEmail();

        email.MarkFailed(Now, "550 mailbox unavailable");

        Assert.Equal(EmailStatus.Failed, email.Status);
        Assert.Equal(1, email.Attempts);
        Assert.Equal("550 mailbox unavailable", email.LastError);
        Assert.Null(email.LockedUntil);
    }

    // ---------- Cancel: Pending -> Cancelled ----------

    [Fact]
    public void Cancel_FromPending_SetsCancelled()
    {
        var email = NewEmail();

        email.Cancel(Now.AddSeconds(1));

        Assert.Equal(EmailStatus.Cancelled, email.Status);
        Assert.Equal(Now.AddSeconds(1), email.UpdatedAt);
    }

    // ---------- RetryManually: Failed -> Pending ----------

    [Fact]
    public void RetryManually_FromFailed_ReturnsToPendingWithAttemptsReset()
    {
        var email = FailedEmail();

        email.RetryManually(Now.AddHours(1));

        Assert.Equal(EmailStatus.Pending, email.Status);
        Assert.Equal(0, email.Attempts);
        Assert.Null(email.NextAttemptAt);
        Assert.Equal(Now.AddHours(1), email.UpdatedAt);
    }

    // ---------- Transições inválidas ----------

    public static TheoryData<string, Func<Email>, Action<Email>> InvalidTransitions => new()
    {
        { "StartProcessing em Processing", () => ProcessingEmail(), e => e.StartProcessing(Now, Now.AddMinutes(1)) },
        { "StartProcessing em Failed", FailedEmail, e => e.StartProcessing(Now, Now.AddMinutes(1)) },
        { "MarkSent em Pending", () => NewEmail(), e => e.MarkSent(Now, "id") },
        { "MarkSent em Failed", FailedEmail, e => e.MarkSent(Now, "id") },
        { "ScheduleRetry em Pending", () => NewEmail(), e => e.ScheduleRetry(Now, Now.AddMinutes(1), "x") },
        { "MarkFailed em Pending", () => NewEmail(), e => e.MarkFailed(Now, "x") },
        { "MarkFailed em Failed", FailedEmail, e => e.MarkFailed(Now, "x") },
        { "Cancel em Processing", () => ProcessingEmail(), e => e.Cancel(Now) },
        { "Cancel em Failed", FailedEmail, e => e.Cancel(Now) },
        { "RetryManually em Pending", () => NewEmail(), e => e.RetryManually(Now) },
        { "RetryManually em Processing", () => ProcessingEmail(), e => e.RetryManually(Now) },
    };

    [Theory]
    [MemberData(nameof(InvalidTransitions))]
    public void InvalidTransition_ThrowsAndKeepsState(string cenario, Func<Email> arrange, Action<Email> act)
    {
        var email = arrange();
        var statusBefore = email.Status;

        var ex = Assert.Throws<InvalidEmailStatusTransitionException>(() => act(email));

        Assert.Equal(statusBefore, ex.From);
        Assert.Equal(statusBefore, email.Status);
        Assert.False(string.IsNullOrEmpty(cenario));
    }

    [Theory]
    [InlineData(EmailStatus.Sent)]
    [InlineData(EmailStatus.Cancelled)]
    public void TerminalStates_RejectEveryTransition(EmailStatus terminal)
    {
        Email Arrange()
        {
            if (terminal == EmailStatus.Cancelled)
            {
                var c = NewEmail();
                c.Cancel(Now);
                return c;
            }
            var s = ProcessingEmail();
            s.MarkSent(Now, "id");
            return s;
        }

        Assert.Throws<InvalidEmailStatusTransitionException>(() => Arrange().StartProcessing(Now, Now.AddMinutes(1)));
        Assert.Throws<InvalidEmailStatusTransitionException>(() => Arrange().MarkSent(Now, "id"));
        Assert.Throws<InvalidEmailStatusTransitionException>(() => Arrange().ScheduleRetry(Now, Now.AddMinutes(1), "x"));
        Assert.Throws<InvalidEmailStatusTransitionException>(() => Arrange().MarkFailed(Now, "x"));
        Assert.Throws<InvalidEmailStatusTransitionException>(() => Arrange().Cancel(Now));
        Assert.Throws<InvalidEmailStatusTransitionException>(() => Arrange().RetryManually(Now));
    }
}
