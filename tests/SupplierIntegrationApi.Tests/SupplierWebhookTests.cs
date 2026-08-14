using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupplierIntegrationApi.Data;
using SupplierIntegrationApi.Entities;
using SupplierIntegrationApi.Enums;

namespace SupplierIntegrationApi.Tests;

public sealed class SupplierWebhookTests
{
    private const string InventoryBody =
        "{\"eventType\":\"inventory.updated\",\"productId\":\"SUP-1001\",\"stockQuantity\":18}";

    [Fact]
    public async Task ExactRawBodySignatureIsAcceptedAndWhitespaceChangeIsRejected()
    {
        using var factory = new TestWebApplicationFactory();
        await SeedProductAsync(factory);
        using var client = factory.CreateClient();

        var accepted = await SendAsync(client, "evt-exact", InventoryBody, Sign(InventoryBody));
        var reformatted = "{ \"eventType\":\"inventory.updated\",\"productId\":\"SUP-1001\",\"stockQuantity\":18}";
        var rejected = await SendAsync(client, "evt-whitespace", reformatted, Sign(InventoryBody));

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        Assert.Equal(1, await CountEventsAsync(factory));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("md5=0011")]
    [InlineData("sha256=xyz")]
    [InlineData("sha256=0000000000000000000000000000000000000000000000000000000000000000")]
    public async Task MissingMalformedOrWrongSignatureIsUnauthorized(string? signature)
    {
        using var factory = new TestWebApplicationFactory();
        using var client = factory.CreateClient();
        var response = await SendAsync(client, "evt-bad-signature", InventoryBody, signature);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await CountEventsAsync(factory));
        var problem = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(TestWebApplicationFactory.WebhookSecret, problem);
        Assert.DoesNotContain(InventoryBody, problem);
        if (!string.IsNullOrEmpty(signature)) Assert.DoesNotContain(signature, problem);
    }

    [Fact]
    public async Task ValidSignatureWithoutEventIdIsBadRequest()
    {
        using var factory = new TestWebApplicationFactory();
        using var client = factory.CreateClient();
        var response = await SendAsync(client, null, InventoryBody, Sign(InventoryBody));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await CountEventsAsync(factory));
    }

    [Fact]
    public async Task MalformedJsonAfterValidSignatureIsBadRequest()
    {
        using var factory = new TestWebApplicationFactory();
        using var client = factory.CreateClient();
        const string body = "{not-json";
        var response = await SendAsync(client, "evt-json", body, Sign(body));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await CountEventsAsync(factory));
    }

    [Theory]
    [InlineData("{\"eventType\":\"inventory.updated\",\"productId\":\"SUP-1001\",\"stockQuantity\":-1}")]
    [InlineData("{\"eventType\":\"price.updated\",\"productId\":\"SUP-1001\",\"price\":0}")]
    [InlineData("{\"eventType\":\"price.updated\",\"productId\":\"SUP-1001\",\"price\":-1}")]
    public async Task InvalidSupportedPayloadIsBadRequestAndDoesNotClaimEvent(string body)
    {
        using var factory = new TestWebApplicationFactory();
        using var client = factory.CreateClient();
        var invalid = await SendAsync(client, "evt-correctable", body, Sign(body));
        Assert.Equal(0, await CountEventsAsync(factory));
        await SeedProductAsync(factory);
        var corrected = await SendAsync(client, "evt-correctable", InventoryBody, Sign(InventoryBody));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);
        Assert.Contains("\"outcome\":\"processed\"", await corrected.Content.ReadAsStringAsync());
        Assert.Equal(1, await CountEventsAsync(factory));
    }

    [Fact]
    public async Task InventoryUpdatedChangesStockAndPreservesLastSynced()
    {
        using var factory = new TestWebApplicationFactory();
        var original = await SeedProductAsync(factory);
        using var client = factory.CreateClient();
        var response = await SendAsync(client, "evt-inventory", InventoryBody, Sign(InventoryBody));
        var (product, webhook) = await LoadAsync(factory, "evt-inventory");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(18, product.StockQuantity);
        Assert.True(product.UpdatedAtUtc > original.UpdatedAtUtc);
        Assert.Equal(original.LastSyncedAtUtc, product.LastSyncedAtUtc);
        Assert.Equal(WebhookEventStatus.Processed, webhook.Status);
        Assert.NotNull(webhook.ProcessedAtUtc);
    }

    [Fact]
    public async Task PriceUpdatedChangesOnlyPriceAndPreservesLastSynced()
    {
        using var factory = new TestWebApplicationFactory();
        var original = await SeedProductAsync(factory);
        using var client = factory.CreateClient();
        const string body = "{\"eventType\":\"price.updated\",\"productId\":\"SUP-1001\",\"price\":89.95}";
        var response = await SendAsync(client, "evt-price", body, Sign(body));
        var (product, webhook) = await LoadAsync(factory, "evt-price");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(89.95m, product.Price);
        Assert.Equal(original.LastSyncedAtUtc, product.LastSyncedAtUtc);
        Assert.Equal(WebhookEventStatus.Processed, webhook.Status);
    }

    [Fact]
    public async Task ProductUpdatedChangesOnlySuppliedFields()
    {
        using var factory = new TestWebApplicationFactory();
        var original = await SeedProductAsync(factory);
        using var client = factory.CreateClient();
        const string body = "{\"eventType\":\"product.updated\",\"productId\":\"SUP-1001\",\"name\":\"Changed\",\"isActive\":false}";
        var response = await SendAsync(client, "evt-product", body, Sign(body));
        var (product, _) = await LoadAsync(factory, "evt-product");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Changed", product.Name);
        Assert.False(product.IsActive);
        Assert.Equal(original.Price, product.Price);
        Assert.Equal(original.StockQuantity, product.StockQuantity);
        Assert.Equal(original.LastSyncedAtUtc, product.LastSyncedAtUtc);
    }

    [Fact]
    public async Task UnknownProductIsPersistedAsIgnoredWithoutCreatingProduct()
    {
        using var factory = new TestWebApplicationFactory();
        using var client = factory.CreateClient();
        var response = await SendAsync(client, "evt-unknown-product", InventoryBody, Sign(InventoryBody));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var webhook = await db.WebhookEvents.SingleAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await db.Products.ToListAsync());
        Assert.Equal(WebhookEventStatus.Ignored, webhook.Status);
        Assert.Equal("unknown_product", webhook.FailureCode);
        Assert.Contains("\"outcome\":\"ignored\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UnknownEventTypeIsIgnoredWithoutProductMutation()
    {
        using var factory = new TestWebApplicationFactory();
        var original = await SeedProductAsync(factory);
        using var client = factory.CreateClient();
        const string body = "{\"eventType\":\"supplier.ping\",\"productId\":\"SUP-1001\"}";
        var response = await SendAsync(client, "evt-unknown-type", body, Sign(body));
        var (product, webhook) = await LoadAsync(factory, "evt-unknown-type");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(WebhookEventStatus.Ignored, webhook.Status);
        Assert.Equal("unsupported_event_type", webhook.FailureCode);
        Assert.Equal(original.UpdatedAtUtc, product.UpdatedAtUtc);
    }

    [Fact]
    public async Task SequentialDuplicateReturnsStableSuccessAndMutatesOnce()
    {
        using var factory = new TestWebApplicationFactory();
        await SeedProductAsync(factory);
        using var client = factory.CreateClient();
        var first = await SendAsync(client, "evt-duplicate", InventoryBody, Sign(InventoryBody));
        var second = await SendAsync(client, "evt-duplicate", InventoryBody, Sign(InventoryBody));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Contains("\"duplicate\":true", await second.Content.ReadAsStringAsync());
        Assert.Equal(1, await CountEventsAsync(factory));
        Assert.Equal(1, factory.Services.GetRequiredService<ProductUpdateCountingInterceptor>().ProductUpdates);
    }

    [Fact]
    public async Task TwentyConcurrentDuplicatesProduceOneEventAndOneProductModification()
    {
        using var factory = new TestWebApplicationFactory();
        await SeedProductAsync(factory);
        using var client = factory.CreateClient();
        var signature = Sign(InventoryBody);
        var responses = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => SendAsync(client, "evt-concurrent", InventoryBody, signature)));
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        Assert.Equal(1, await CountEventsAsync(factory));
        Assert.Equal(1, factory.Services.GetRequiredService<ProductUpdateCountingInterceptor>().ProductUpdates);
        Assert.Single(responses, response =>
            response.Content.ReadAsStringAsync().GetAwaiter().GetResult().Contains("\"duplicate\":false"));
    }

    [Fact]
    public async Task DifferentEventIdsAreDistinctEvenWithSameBody()
    {
        using var factory = new TestWebApplicationFactory();
        await SeedProductAsync(factory);
        using var client = factory.CreateClient();
        var first = await SendAsync(client, "evt-one", InventoryBody, Sign(InventoryBody));
        var second = await SendAsync(client, "evt-two", InventoryBody, Sign(InventoryBody));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(2, await CountEventsAsync(factory));
    }

    [Fact]
    public async Task OversizedBodyIsRejectedBeforeSignatureOrSideEffects()
    {
        using var factory = new TestWebApplicationFactory();
        await SeedProductAsync(factory);
        using var client = factory.CreateClient();
        var body = new string('x', 64 * 1024 + 1);
        var response = await SendAsync(client, "evt-large", body, Sign(body));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, await CountEventsAsync(factory));
        Assert.Equal(0, factory.Services.GetRequiredService<ProductUpdateCountingInterceptor>().ProductUpdates);
    }

    [Fact]
    public async Task UnrelatedDatabaseFailureIsNotClassifiedAsDuplicateAndReturnsSafeProblemDetails()
    {
        using var factory = new TestWebApplicationFactory(failWebhookClaim: true);
        await SeedProductAsync(factory);
        using var client = factory.CreateClient();
        var signature = Sign(InventoryBody);

        var response = await SendAsync(client, "evt-database-failure", InventoryBody, signature);
        var problem = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("\"duplicate\":true", problem);
        Assert.DoesNotContain("test-provider-detail-sentinel", problem);
        Assert.DoesNotContain(TestWebApplicationFactory.WebhookSecret, problem);
        Assert.DoesNotContain(signature, problem);
        Assert.DoesNotContain(InventoryBody, problem);
        Assert.Equal(0, await CountEventsAsync(factory));
        Assert.Equal(0, factory.Services.GetRequiredService<ProductUpdateCountingInterceptor>().ProductUpdates);
    }

    [Fact]
    public async Task OpenApiDocumentsAnonymousWebhookHeadersAndProtectedAdminEndpoint()
    {
        using var factory = new TestWebApplicationFactory();
        using var client = factory.CreateClient();
        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var root = document.RootElement;
        var webhook = root.GetProperty("paths").GetProperty("/api/webhooks/supplier").GetProperty("post");
        Assert.False(webhook.TryGetProperty("security", out var webhookSecurity) && webhookSecurity.GetArrayLength() > 0);
        var headers = webhook.GetProperty("parameters").EnumerateArray()
            .Select(parameter => parameter.GetProperty("name").GetString()).ToArray();
        Assert.Contains("X-Supplier-Event-Id", headers);
        Assert.Contains("X-Supplier-Signature", headers);
        Assert.All(webhook.GetProperty("parameters").EnumerateArray(), parameter =>
            Assert.True(parameter.GetProperty("required").GetBoolean()));
        Assert.True(webhook.GetProperty("requestBody").GetProperty("required").GetBoolean());
        Assert.True(webhook.GetProperty("requestBody").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema").GetProperty("properties")
            .TryGetProperty("eventType", out _));
        var admin = root.GetProperty("paths").GetProperty("/api/admin/integrations/supplier/sync").GetProperty("post");
        Assert.True(admin.GetProperty("security").GetArrayLength() > 0);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string? eventId, string body, string? signature)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/supplier")
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body))
        };
        request.Content.Headers.ContentType = new("application/json");
        if (eventId is not null) request.Headers.TryAddWithoutValidation("X-Supplier-Event-Id", eventId);
        if (signature is not null) request.Headers.TryAddWithoutValidation("X-Supplier-Signature", signature);
        return await client.SendAsync(request);
    }

    private static string Sign(string rawBody)
    {
        var digest = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(TestWebApplicationFactory.WebhookSecret),
            Encoding.UTF8.GetBytes(rawBody));
        return $"sha256={Convert.ToHexString(digest).ToLowerInvariant()}";
    }

    private static async Task<Product> SeedProductAsync(TestWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var product = new Product
        {
            ExternalId = "SUP-1001", Sku = "SKU-1001", Name = "Original", Price = 10,
            StockQuantity = 2, IsActive = true,
            CreatedAtUtc = DateTime.UtcNow.AddDays(-2), UpdatedAtUtc = DateTime.UtcNow.AddDays(-1),
            LastSyncedAtUtc = DateTime.UtcNow.AddHours(-1)
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product;
    }

    private static async Task<int> CountEventsAsync(TestWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().WebhookEvents.CountAsync();
    }

    private static async Task<(Product Product, WebhookEvent Webhook)> LoadAsync(
        TestWebApplicationFactory factory, string eventId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.Products.AsNoTracking().SingleAsync(),
            await db.WebhookEvents.AsNoTracking().SingleAsync(item => item.ExternalEventId == eventId));
    }
}
