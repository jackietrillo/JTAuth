using JTAuth.Application;
using JTAuth.BuildingBlocks.Registration;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHandlers(AuthApplication.Assembly);
builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready");

await app.RunAsync().ConfigureAwait(false);
