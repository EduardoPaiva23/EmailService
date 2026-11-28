using EmailService.Application.Exceptions;
using EmailService.Domain.Exceptions;
using Microsoft.AspNetCore.Diagnostics;

namespace EmailService.API.Errors;

/// <summary>Converte exceções conhecidas em respostas ProblemDetails (RFC 9457).</summary>
public sealed class ProblemDetailsExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        (int Status, string Title)? mapped = exception switch
        {
            IdempotencyConflictException => (StatusCodes.Status422UnprocessableEntity, "Idempotency-Key reutilizada"),
            InvalidEmailStatusTransitionException => (StatusCodes.Status409Conflict, "Transição de status inválida"),
            DomainException => (StatusCodes.Status400BadRequest, "Solicitação inválida"),
            _ => null
        };
        if (mapped is null)
            return false; // 500 padrão, sem detalhes internos

        context.Response.StatusCode = mapped.Value.Status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            Exception = exception,
            ProblemDetails = { Status = mapped.Value.Status, Title = mapped.Value.Title, Detail = exception.Message }
        });
    }
}
