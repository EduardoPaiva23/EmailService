using System.Security.Claims;
using System.Text.Encodings.Web;
using EmailService.Application.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace EmailService.API.Authentication;

/// <summary>Autentica o cliente pelo header <c>X-Api-Key</c>.</summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ApiKeyAuthenticator authenticator,
    IProblemDetailsService problemDetails)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";
    public const string ClientIdClaim = "client_id";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var values) || string.IsNullOrEmpty(values))
            return AuthenticateResult.NoResult();

        var clientId = await authenticator.AuthenticateAsync(values.ToString(), Context.RequestAborted);
        if (clientId is null)
            return AuthenticateResult.Fail("API key inválida ou inativa.");

        var identity = new ClaimsIdentity([new Claim(ClientIdClaim, clientId.Value.ToString())], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        await problemDetails.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = Context,
            ProblemDetails =
            {
                Status = StatusCodes.Status401Unauthorized,
                Title = "Não autenticado",
                Detail = $"Informe uma API key válida no header {HeaderName}."
            }
        });
    }
}

public static class ClaimsPrincipalExtensions
{
    public static Guid GetClientId(this ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue(ApiKeyAuthenticationHandler.ClientIdClaim)
            ?? throw new InvalidOperationException("Usuário sem claim de cliente."));
}
