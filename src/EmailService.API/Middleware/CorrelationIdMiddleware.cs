using Serilog.Context;

namespace EmailService.API.Middleware;

/// <summary>
/// Lê <c>X-Correlation-Id</c> (ou gera um novo), devolve no header da
/// resposta e o adiciona a todos os logs da requisição.
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next, IProblemDetailsService problemDetails)
{
    public const string HeaderName = "X-Correlation-Id";
    private const string ItemKey = "CorrelationId";

    public async Task InvokeAsync(HttpContext context)
    {
        Guid correlationId;
        var header = context.Request.Headers[HeaderName].ToString();
        if (string.IsNullOrEmpty(header))
        {
            correlationId = Guid.NewGuid();
        }
        else if (!Guid.TryParse(header, out correlationId))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await problemDetails.WriteAsync(new ProblemDetailsContext
            {
                HttpContext = context,
                ProblemDetails =
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Header inválido",
                    Detail = $"{HeaderName} deve ser um GUID."
                }
            });
            return;
        }

        context.Items[ItemKey] = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId.ToString();
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty("CorrelationId", correlationId))
            await next(context);
    }

    public static Guid Get(HttpContext context) =>
        context.Items[ItemKey] as Guid? ?? throw new InvalidOperationException("CorrelationIdMiddleware não executado.");
}
