using System.Text.Json.Serialization;
using EmailService.API.Authentication;
using EmailService.API.Errors;
using EmailService.API.Middleware;
using EmailService.Application;
using EmailService.Application.UseCases.SendEmail;
using EmailService.Infrastructure;
using EmailService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSerilog((services, logger) => logger
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {CorrelationId} {Message:lj}{NewLine}{Exception}"));

// Registrado antes de AddApplication, que só usa o padrão se nada foi configurado.
builder.Services.AddSingleton(
    builder.Configuration.GetSection(SendEmailOptions.SectionName).Get<SendEmailOptions>() ?? new SendEmailOptions());

builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration);

builder.Services
    .AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null);
builder.Services.AddAuthorization();

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ProblemDetailsExceptionHandler>();

builder.Services
    .AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi();

var app = builder.Build();

// Ordem importa: o log de requisição fica por fora do tratamento de exceções,
// para registrar o status final (ex.: 422) e não a exceção como 500.
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseSerilogRequestLogging();
app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();

    // Cria o ApiClient de desenvolvimento (DevelopmentSeed:ApiKey, via User Secrets).
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<DevelopmentDataSeeder>().SeedAsync();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();
