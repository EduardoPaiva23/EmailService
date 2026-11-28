using EmailService.API.Authentication;
using EmailService.API.Middleware;
using EmailService.Application.UseCases.GetEmailStatus;
using EmailService.Application.UseCases.SendEmail;
using EmailService.Domain.Enums;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EmailService.API.Controllers;

/// <summary>Corpo do POST. Campos anuláveis: a validação fica no FluentValidation.</summary>
public sealed record SendEmailRequest(string? To, string? Subject, string? Body, bool IsHtml);

public sealed record EmailAcceptedResponse(Guid Id, EmailStatus Status, DateTime CreatedAt);

[ApiController]
[Route("api/v1/emails")]
[Authorize]
public sealed class EmailsController : ControllerBase
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";
    public const string ReplayedHeader = "Idempotent-Replayed";

    [HttpPost]
    [ProducesResponseType<EmailAcceptedResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Send(
        [FromBody] SendEmailRequest request,
        [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKey,
        [FromServices] IValidator<SendEmailCommand> validator,
        [FromServices] SendEmailHandler handler,
        CancellationToken cancellationToken)
    {
        var command = new SendEmailCommand(
            User.GetClientId(),
            idempotencyKey ?? "",
            CorrelationIdMiddleware.Get(HttpContext),
            request.To ?? "",
            request.Subject ?? "",
            request.Body ?? "",
            request.IsHtml);

        var validation = await validator.ValidateAsync(command, cancellationToken);
        if (!validation.IsValid)
            return ValidationProblem(new ValidationProblemDetails(validation.ToDictionary()));

        var result = await handler.HandleAsync(command, cancellationToken);
        if (result.Replayed)
            Response.Headers[ReplayedHeader] = "true";

        return Accepted($"/api/v1/emails/{result.Id}", new EmailAcceptedResponse(result.Id, result.Status, result.CreatedAt));
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<EmailStatusResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(
        Guid id,
        [FromServices] GetEmailStatusHandler handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new GetEmailStatusQuery(User.GetClientId(), id), cancellationToken);

        // E-mail de outro cliente responde igual a inexistente: não revela que o id existe.
        return result is null
            ? Problem(statusCode: StatusCodes.Status404NotFound, title: "E-mail não encontrado")
            : Ok(result);
    }
}
