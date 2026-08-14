using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
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

    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly HttpMessageHandler? supplierHandler;

    public TestWebApplicationFactory(HttpMessageHandler? supplierHandler = null)
    {
        this.supplierHandler = supplierHandler;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        connection.Open();
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
                ["Supplier:RequestTimeoutSeconds"] = "0.05"
            });
        });
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<AppDbContext>();
            services.RemoveAll<DbContextOptions>();
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<AppDbContext>>();
            services.AddDbContext<AppDbContext>(options => options.UseSqlite(connection));
            if (supplierHandler is not null)
            {
                services.AddHttpClient<ISupplierClient, SupplierIntegrationApi.Services.SupplierClient>()
                    .ConfigurePrimaryHttpMessageHandler(() => supplierHandler);
            }

            using var scope = services.BuildServiceProvider().CreateScope();
            scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
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
        if (disposing)
        {
            connection.Dispose();
        }
    }
}
