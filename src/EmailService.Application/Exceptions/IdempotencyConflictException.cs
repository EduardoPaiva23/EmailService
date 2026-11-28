namespace EmailService.Application.Exceptions;

/// <summary>A Idempotency-Key já foi usada por este cliente com outro payload (HTTP 422).</summary>
public sealed class IdempotencyConflictException(string idempotencyKey)
    : Exception($"A Idempotency-Key '{idempotencyKey}' já foi usada com um payload diferente.")
{
    public string IdempotencyKey { get; } = idempotencyKey;
}
