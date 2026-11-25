using EmailService.Domain.Enums;
using EmailService.Domain.Exceptions;

namespace EmailService.Domain.Entities;

/// <summary>Registro histórico de uma tentativa de envio.</summary>
public sealed class EmailAttempt
{
    public Guid Id { get; private set; }
    public Guid EmailId { get; private set; }
    public int AttemptNumber { get; private set; }
    public DateTime StartedAt { get; private set; }
    public DateTime? FinishedAt { get; private set; }
    public int? DurationMs { get; private set; }
    public AttemptOutcome? Outcome { get; private set; }
    public int? SmtpStatusCode { get; private set; }
    public string? ErrorMessage { get; private set; }
    public string WorkerId { get; private set; } = null!;

    private EmailAttempt() { } // EF Core

    public static EmailAttempt Start(Guid emailId, int attemptNumber, DateTime startedAt, string workerId)
    {
        if (attemptNumber < 1)
            throw new DomainException("AttemptNumber deve ser pelo menos 1.");
        if (string.IsNullOrWhiteSpace(workerId))
            throw new DomainException("'workerId' é obrigatório.");

        return new EmailAttempt
        {
            Id = Guid.NewGuid(),
            EmailId = emailId,
            AttemptNumber = attemptNumber,
            StartedAt = startedAt,
            WorkerId = workerId
        };
    }

    public void Finish(DateTime finishedAt, AttemptOutcome outcome, int? smtpStatusCode = null, string? errorMessage = null)
    {
        if (FinishedAt is not null)
            throw new DomainException("A tentativa já foi finalizada.");
        if (finishedAt < StartedAt)
            throw new DomainException("FinishedAt não pode ser anterior a StartedAt.");

        FinishedAt = finishedAt;
        DurationMs = (int)(finishedAt - StartedAt).TotalMilliseconds;
        Outcome = outcome;
        SmtpStatusCode = smtpStatusCode;
        ErrorMessage = errorMessage;
    }
}
