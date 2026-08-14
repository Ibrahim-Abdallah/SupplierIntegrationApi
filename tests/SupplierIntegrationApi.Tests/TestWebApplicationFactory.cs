using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SupplierIntegrationApi.Data;
using SupplierIntegrationApi.Entities;
using SupplierIntegrationApi.Enums;
using SupplierIntegrationApi.Interfaces;

namespace SupplierIntegrationApi.Tests;

public sealed class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string JwtIssuer = "SupplierIntegrationApi.Tests";
    public const string JwtAudience = "SupplierIntegrationApi.Tests.Client";
    public const string JwtKey = "test-only-signing-key-with-at-least-32-bytes";
    public const string WebhookSecret = "test-only-webhook-secret-at-least-32-bytes";

    private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"supplier-webhooks-{Guid.NewGuid():N}.db");
    private readonly HttpMessageHandler? supplierHandler;
    private readonly bool failWebhookClaim;
    private readonly bool scheduledSyncEnabled;

    public TestWebApplicationFactory(
        HttpMessageHandler? supplierHandler = null,
        bool failWebhookClaim = false,
        bool scheduledSyncEnabled = false)
    {
        this.supplierHandler = supplierHandler;
        this.failWebhookClaim = failWebhookClaim;
        this.scheduledSyncEnabled = scheduledSyncEnabled;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Issuer"] = JwtIssuer,
                ["Jwt:Audience"] = JwtAudience,
                ["Jwt:Key"] = JwtKey,
                ["Jwt:AccessTokenLifetimeMinutes"] = "15",
                ["Supplier:BaseUrl"] = "https://supplier.test/",
                ["Supplier:ApiKey"] = "test-only-api-key",
                ["Supplier:PageSize"] = "2",
                ["Supplier:RequestTimeoutSeconds"] = "0.05",
                ["Supplier:WebhookSecret"] = WebhookSecret,
                ["Supplier:ScheduledSyncEnabled"] = scheduledSyncEnabled.ToString(),
                ["Supplier:ScheduledSyncIntervalMinutes"] = "30"
            });
        });
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<AppDbContext>();
            services.RemoveAll<DbContextOptions>();
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>();
            services.AddSingleton<ProductUpdateCountingInterceptor>();
            if (failWebhookClaim) services.AddSingleton<WebhookClaimFailureInterceptor>();
            services.AddDbContext<AppDbContext>((provider, options) => options
                .UseSqlite($"Data Source={databasePath};Cache=Shared;Default Timeout=30;Pooling=False")
                .AddInterceptors(failWebhookClaim
                    ? [provider.GetRequiredService<ProductUpdateCountingInterceptor>(),
                        provider.GetRequiredService<WebhookClaimFailureInterceptor>()]
                    : [provider.GetRequiredService<ProductUpdateCountingInterceptor>()]));
            if (supplierHandler is not null)
            {
                services.AddHttpClient<ISupplierClient, SupplierIntegrationApi.Services.SupplierClient>()
                    .ConfigurePrimaryHttpMessageHandler(() => supplierHandler);
            }

            using var scope = services.BuildServiceProvider().CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<AppDbContext>().Database;
            database.EnsureCreated();
            database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
            database.ExecuteSqlRaw("PRAGMA busy_timeout=30000;");
        });
    }

    public async Task<User> SeedAdminAsync(
        string email = "admin@example.com",
        string password = "Correct-Horse-Battery-Staple",
        bool isActive = true)
    {
        using var scope = Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var normalizer = scope.ServiceProvider.GetRequiredService<IEmailNormalizer>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
        var user = new User
        {
            Email = email.Trim(),
            NormalizedEmail = normalizer.Normalize(email),
            Role = UserRole.Admin,
            IsActive = isActive,
            CreatedAtUtc = DateTime.UtcNow
        };
        user.PasswordHash = hasher.HashPassword(user, password);
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        return user;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && File.Exists(databasePath)) File.Delete(databasePath);
    }
}

public sealed class ProductUpdateCountingInterceptor : SaveChangesInterceptor
{
    private int productUpdates;
    public int ProductUpdates => Volatile.Read(ref productUpdates);

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null)
        {
            var count = eventData.Context.ChangeTracker.Entries<Product>()
                .Count(entry => entry.State == EntityState.Modified);
            Interlocked.Add(ref productUpdates, count);
        }
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}

public sealed class WebhookClaimFailureInterceptor : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context?.ChangeTracker.Entries<WebhookEvent>()
            .Any(entry => entry.State == EntityState.Added) == true)
        {
            throw new DbUpdateException("test-provider-detail-sentinel");
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}
