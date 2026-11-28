using EmailService.Application.Abstractions.Persistence;
using EmailService.Domain.Enums;

namespace EmailService.Application.UseCases.GetEmailStatus;

public sealed record GetEmailStatusQuery(Guid ClientId, Guid EmailId);

public sealed record EmailStatusResult(
    Guid Id,
    EmailStatus Status,
    string To,
    string Subject,
    int Attempts,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? SentAt,
    string? LastError);

public sealed class GetEmailStatusHandler(IEmailRepository emails)
{
    /// <returns>null se o e-mail não existe ou pertence a outro cliente.</returns>
    public async Task<EmailStatusResult?> HandleAsync(GetEmailStatusQuery query, CancellationToken cancellationToken = default)
    {
        var email = await emails.GetByIdAsync(query.EmailId, cancellationToken);
        if (email is null || email.ClientId != query.ClientId)
            return null;

        return new EmailStatusResult(
            email.Id, email.Status, email.Recipient, email.Subject, email.Attempts,
            email.CreatedAt, email.UpdatedAt, email.SentAt, email.LastError);
    }
}
