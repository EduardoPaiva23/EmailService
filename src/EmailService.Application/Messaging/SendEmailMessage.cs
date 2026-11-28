using System.Text.Json;

namespace EmailService.Application.Messaging;

/// <summary>
/// Evento publicado no RabbitMQ (via Outbox). É pequeno de propósito: o
/// Worker busca o conteúdo do e-mail no banco.
/// </summary>
public sealed record SendEmailMessage(Guid EmailId, Guid CorrelationId, DateTime CreatedAt)
{
    /// <summary>Valor de OutboxMessage.Type para este evento.</summary>
    public const string Type = "SendEmail";

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}
