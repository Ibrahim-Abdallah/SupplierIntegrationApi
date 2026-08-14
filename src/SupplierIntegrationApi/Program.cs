using System.Security.Claims;
using System.Text;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Polly;
using Polly.Timeout;
using Scalar.AspNetCore;
using SupplierIntegrationApi.Configuration;
using SupplierIntegrationApi.Data;
using SupplierIntegrationApi.Entities;
using SupplierIntegrationApi.Interfaces;
using SupplierIntegrationApi.Services;
using SupplierIntegrationApi.Validation;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "Enter a JWT access token."
        };
        return Task.CompletedTask;
    });
    options.AddOperationTransformer((operation, context, _) =>
    {
        var requiresAuthorization = context.Description.ActionDescriptor.EndpointMetadata
            .OfType<IAuthorizeData>()
            .Any();
        var allowsAnonymous = context.Description.ActionDescriptor.EndpointMetadata
            .OfType<IAllowAnonymous>()
            .Any();
        if (requiresAuthorization && !allowsAnonymous)
        {
            operation.Security ??= [];
            operation.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("Bearer", context.Document)] = []
            });
        }
        if (context.Description.ActionDescriptor is ControllerActionDescriptor
            { ControllerName: "SupplierWebhooks", ActionName: "Receive" })
        {
            operation.Summary = "Receive a signed supplier webhook";
            operation.Description = "The signature is HMAC-SHA256 over the exact raw request body bytes.";
            operation.RequestBody = new OpenApiRequestBody
            {
                Required = true,
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/json"] = new()
                    {
                        Schema = new OpenApiSchema
                        {
                            Type = JsonSchemaType.Object,
                            Properties = new Dictionary<string, IOpenApiSchema>
                            {
                                ["eventType"] = new OpenApiSchema { Type = JsonSchemaType.String },
                                ["productId"] = new OpenApiSchema { Type = JsonSchemaType.String },
                                ["stockQuantity"] = new OpenApiSchema { Type = JsonSchemaType.Integer },
                                ["price"] = new OpenApiSchema { Type = JsonSchemaType.Number },
                                ["name"] = new OpenApiSchema { Type = JsonSchemaType.String },
                                ["isActive"] = new OpenApiSchema { Type = JsonSchemaType.Boolean }
                            }
                        }
                    }
                }
            };
            foreach (var parameter in operation.Parameters?.OfType<OpenApiParameter>() ?? [])
            {
                if (parameter.Name is "X-Supplier-Event-Id" or "X-Supplier-Signature")
                    parameter.Required = true;
            }
        }
        return Task.CompletedTask;
    });
});
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.AddOptions<SupplierOptions>()
    .Bind(builder.Configuration.GetSection(SupplierOptions.SectionName))
    .Validate(options => options.BaseUrl.IsAbsoluteUri && options.BaseUrl.Scheme is "http" or "https",
        "Supplier BaseUrl must be an absolute HTTP or HTTPS URL.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.ApiKey), "Supplier API key is required.")
    .Validate(options => options.PageSize is >= 1 and <= 1000, "Supplier page size must be between 1 and 1000.")
    .Validate(options => options.RequestTimeoutSeconds is >= 0.05 and <= 120,
        "Supplier request timeout must be between 0.05 and 120 seconds.")
    .Validate(options => Encoding.UTF8.GetByteCount(options.WebhookSecret) >= 32,
        "Supplier webhook secret must be at least 32 UTF-8 bytes.")
    .ValidateOnStart();
builder.Services.Configure<AdminSeedOptions>(
    builder.Configuration.GetSection(AdminSeedOptions.SectionName));
builder.Services.AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.Issuer), "JWT issuer is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.Audience), "JWT audience is required.")
    .Validate(options => Encoding.UTF8.GetByteCount(options.Key) >= 32, "JWT key must be at least 256 bits.")
    .Validate(options => options.AccessTokenLifetimeMinutes is >= 1 and <= 1440,
        "JWT access-token lifetime must be between 1 and 1440 minutes.")
    .ValidateOnStart();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<JwtOptions>>((options, configuredJwtOptions) =>
    {
        var jwtOptions = configuredJwtOptions.Value;
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Key)),
            RequireSignedTokens = true,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.Zero,
            NameClaimType = ClaimTypes.NameIdentifier,
            RoleClaimType = ClaimTypes.Role
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IEmailNormalizer, EmailNormalizer>();
builder.Services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddScoped<IJwtTokenService, JwtTokenService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<ISupplierSyncService, SupplierSyncService>();
builder.Services.AddSingleton<IWebhookSignatureVerifier, WebhookSignatureVerifier>();
builder.Services.AddScoped<ISupplierWebhookService, SupplierWebhookService>();
builder.Services.AddHttpClient<ISupplierClient, SupplierClient>((services, client) =>
    {
        var supplier = services.GetRequiredService<IOptions<SupplierOptions>>().Value;
        client.BaseAddress = supplier.BaseUrl;
        client.DefaultRequestHeaders.Add("X-Api-Key", supplier.ApiKey);
        client.Timeout = Timeout.InfiniteTimeSpan;
    })
    .AddStandardResilienceHandler()
    .Configure((options, services) =>
    {
        var supplier = services.GetRequiredService<IOptions<SupplierOptions>>().Value;
        var resilienceLogger = services.GetRequiredService<ILogger<SupplierClient>>();
        var attemptTimeout = TimeSpan.FromSeconds(supplier.RequestTimeoutSeconds);
        options.Retry.MaxRetryAttempts = 3;
        options.Retry.Delay = TimeSpan.FromMilliseconds(200);
        options.Retry.BackoffType = DelayBackoffType.Constant;
        options.Retry.UseJitter = false;
        options.Retry.ShouldRetryAfterHeader = true;
        options.Retry.OnRetry = arguments =>
        {
            var statusCode = arguments.Outcome.Result?.StatusCode;
            var failureCategory = statusCode is not null
                ? $"http_{(int)statusCode.Value}"
                : arguments.Outcome.Exception is TimeoutRejectedException
                    ? "timeout"
                    : "network";
            resilienceLogger.LogWarning(
                "Retrying supplier request after {FailureCategory}; retry {RetryAttempt} of {MaxRetryAttempts} in {RetryDelayMs} ms",
                failureCategory, arguments.AttemptNumber + 1, options.Retry.MaxRetryAttempts,
                arguments.RetryDelay.TotalMilliseconds);
            return default;
        };
        options.AttemptTimeout.Timeout = attemptTimeout;
        options.TotalRequestTimeout.Timeout = (attemptTimeout * 4) + TimeSpan.FromSeconds(2);
    });
builder.Services.AddValidatorsFromAssemblyContaining<LoginRequestValidator>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing"))
{
    app.MapOpenApi();
}

if (app.Environment.IsDevelopment())
{
    app.MapScalarApiReference();
}

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health");

await DevelopmentAdminSeeder.SeedAsync(app);

app.Run();

public partial class Program;
