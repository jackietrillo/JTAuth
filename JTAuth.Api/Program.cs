using JTAuth.Api;
using JTAuth.Application;
using JTAuth.BuildingBlocks.Registration;
using JTAuth.Infrastructure;
using JTAuth.Infrastructure.Email;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);

var settings = builder.Configuration.GetSection(JTAuthSettings.SectionName).Get<JTAuthSettings>() ?? new JTAuthSettings();
var rateLimits = builder.Configuration.GetSection(RateLimitSettings.SectionName).Get<RateLimitSettings>() ?? new RateLimitSettings();

var connectionString = builder.Configuration.GetConnectionString("JTAuthDb")
    ?? throw new InvalidOperationException("ConnectionStrings:JTAuthDb is not set.");
if (string.IsNullOrWhiteSpace(settings.Issuer))
{
    throw new InvalidOperationException("JTAuth:Issuer is not set (the API's public base URL).");
}

if (string.IsNullOrWhiteSpace(settings.CodeSecret))
{
    throw new InvalidOperationException("JTAuth:CodeSecret is not set (the secret that keys the stored sign-in code hashes).");
}

// The signing key lives in a file outside the repository, generated on first run. In Development it defaults to the
// current user's application data folder; anywhere else the path must be configured.
var signingKeyFile = builder.Configuration["JTAuth:SigningKeyFile"];
if (string.IsNullOrWhiteSpace(signingKeyFile))
{
    signingKeyFile = builder.Environment.IsDevelopment()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JTAuth", "signing-key.json")
        : throw new InvalidOperationException("JTAuth:SigningKeyFile is not set.");
}

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(rateLimits);
builder.Services.AddJTAuthInfrastructure(connectionString, signingKeyFile);
builder.Services.AddHandlers(AuthApplication.Assembly);

// Codes are written to the log in Development. There is no real email service yet, so any other environment refuses to start
// rather than silently dropping the code or logging it.
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddSingleton<JTAuth.Application.IEmailSender, LoggingEmailSender>();
}
else
{
    throw new InvalidOperationException("No email sender is configured for this environment.");
}

builder.Services.AddJTAuthAuthentication();
builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "JTAuth API", Version = "v1" }));

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = RateLimiting.CreateGlobalLimiter(rateLimits);
    options.OnRejected = async (context, cancellationToken) =>
    {
        if (context.Lease.TryGetMetadata(System.Threading.RateLimiting.MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        await context.HttpContext.Response.WriteAsJsonAsync(
            new ProblemDetails { Status = StatusCodes.Status429TooManyRequests, Title = "Too many requests" },
            options: null, contentType: "application/problem+json", cancellationToken).ConfigureAwait(false);
    };
});

builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database");

var app = builder.Build();

app.UseExceptionHandler();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/swagger/v1/swagger.json", "JTAuth API v1"));
}

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready");
app.MapControllers();

await app.RunAsync().ConfigureAwait(false);

/// <summary>Lets the integration tests start the API in memory.</summary>
public partial class Program;
