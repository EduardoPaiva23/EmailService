using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmailService.Domain.Enums;

namespace EmailService.Application.UseCases.SendEmail;

public sealed record SendEmailCommand(
    Guid ClientId,
    string IdempotencyKey,
    Guid CorrelationId,
    string To,
    string Subject,
    string Body,
    bool IsHtml);

/// <param name="Replayed">True quando a resposta é de uma solicitação anterior com a mesma Idempotency-Key.</param>
public sealed record SendEmailResult(Guid Id, EmailStatus Status, DateTime CreatedAt, bool Replayed);

public sealed class SendEmailOptions
{
    public const string SectionName = "Email";

    public int MaxAttempts { get; init; } = 5;
    public TimeSpan IdempotencyKeyTtl { get; init; } = TimeSpan.FromHours(24);
}

/// <summary>Hash do payload, para detectar reuso da Idempotency-Key com outro conteúdo.</summary>
public static class SendEmailRequestHash
{
    public static string Compute(SendEmailCommand command)
    {
        // Só o conteúdo da solicitação; ClientId, chave e CorrelationId ficam de fora.
        var canonical = JsonSerializer.Serialize(new { command.To, command.Subject, command.Body, command.IsHtml });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
