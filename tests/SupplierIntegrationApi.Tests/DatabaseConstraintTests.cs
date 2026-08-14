using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SupplierIntegrationApi.Data;
using SupplierIntegrationApi.Entities;
using SupplierIntegrationApi.Enums;

namespace SupplierIntegrationApi.Tests;

public class DatabaseConstraintTests
{
    [Fact]
    public async Task DuplicateNormalizedEmailIsRejected()
    {
        await using var database = await TestDatabase.CreateAsync();

        database.Context.Users.Add(CreateUser("admin@example.com"));
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();
        database.Context.Users.Add(CreateUser("admin@example.com"));

        await Assert.ThrowsAsync<DbUpdateException>(() => database.Context.SaveChangesAsync());
    }

    [Fact]
    public async Task DuplicateProductExternalIdIsRejected()
    {
        await using var database = await TestDatabase.CreateAsync();

        database.Context.Products.Add(CreateProduct("SUP-1001", "SKU-1"));
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();
        database.Context.Products.Add(CreateProduct("SUP-1001", "SKU-2"));

        await Assert.ThrowsAsync<DbUpdateException>(() => database.Context.SaveChangesAsync());
    }

    [Fact]
    public async Task DuplicateWebhookExternalEventIdIsRejected()
    {
        await using var database = await TestDatabase.CreateAsync();

        database.Context.WebhookEvents.Add(CreateWebhookEvent("evt-1"));
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();
        database.Context.WebhookEvents.Add(CreateWebhookEvent("evt-1"));

        await Assert.ThrowsAsync<DbUpdateException>(() => database.Context.SaveChangesAsync());
    }

    [Fact]
    public async Task NegativeProductStockIsRejected()
    {
        await using var database = await TestDatabase.CreateAsync();
        var product = CreateProduct("SUP-1001", "SKU-1");
        product.StockQuantity = -1;
        database.Context.Products.Add(product);

        await Assert.ThrowsAsync<DbUpdateException>(() => database.Context.SaveChangesAsync());
    }

    private static User CreateUser(string normalizedEmail) => new()
    {
        Email = normalizedEmail,
        NormalizedEmail = normalizedEmail.ToUpperInvariant(),
        PasswordHash = "$argon2id$v=19$test-hash",
        Role = UserRole.Admin,
        IsActive = true,
        CreatedAtUtc = DateTime.UtcNow
    };

    private static Product CreateProduct(string externalId, string sku) => new()
    {
        ExternalId = externalId,
        Sku = sku,
        Name = "Test product",
        Price = 10.00m,
        StockQuantity = 1,
        IsActive = true,
        LastSyncedAtUtc = DateTime.UtcNow,
        CreatedAtUtc = DateTime.UtcNow,
        UpdatedAtUtc = DateTime.UtcNow
    };

    private static WebhookEvent CreateWebhookEvent(string externalEventId) => new()
    {
        ExternalEventId = externalEventId,
        EventType = "product.updated",
        Status = WebhookEventStatus.Received,
        ReceivedAtUtc = DateTime.UtcNow
    };

    private sealed class TestDatabase(SqliteConnection connection, AppDbContext context) : IAsyncDisposable
    {
        public AppDbContext Context { get; } = context;

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options;
            var context = new AppDbContext(options);
            await context.Database.EnsureCreatedAsync();
            return new TestDatabase(connection, context);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
