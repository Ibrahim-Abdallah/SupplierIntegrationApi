using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupplierIntegrationApi.Data;
using SupplierIntegrationApi.DTOs;
using SupplierIntegrationApi.Entities;
using SupplierIntegrationApi.Enums;

namespace SupplierIntegrationApi.Tests;

public sealed class SupplierSyncTests
{
    [Fact]
    public async Task ManualSyncReadsEveryPageUpsertsAndIsIdempotent()
    {
        var handler = new RecordingHandler((request, _) =>
        {
            Assert.Equal("test-only-api-key", request.Headers.GetValues("X-Api-Key").Single());
            Assert.Equal("/products", request.RequestUri!.AbsolutePath);
            var page = request.RequestUri.Query.Contains("page=2", StringComparison.Ordinal) ? 2 : 1;
            Assert.Contains("pageSize=2", request.RequestUri.Query, StringComparison.Ordinal);
            return Task.FromResult(Json(page == 1
                ? new SupplierProductsPageDto([
                    new("SUP-1", "SKU-1", "First", 10m, 3, true),
                    new("SUP-2", "SKU-2", "Second", 20m, 4, true)], 1, 2, 2)
                : new SupplierProductsPageDto([
                    new("SUP-3", "SKU-3", "Third", 30m, 5, false)], 2, 2, 2)));
        });
        await using var factory = new TestWebApplicationFactory(handler);
        using var client = await AdminClientAsync(factory);

        var firstResponse = await client.PostAsync("/api/admin/integrations/supplier/sync", null);
        var first = await firstResponse.Content.ReadFromJsonAsync<SyncRunResponse>();
        var secondResponse = await client.PostAsync("/api/admin/integrations/supplier/sync", null);
        var second = await secondResponse.Content.ReadFromJsonAsync<SyncRunResponse>();

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal((3, 3, 0, 0), (first!.ItemsRead, first.ItemsCreated, first.ItemsUpdated, first.ItemsUnchanged));
        Assert.Equal((3, 0, 0, 3), (second!.ItemsRead, second.ItemsCreated, second.ItemsUpdated, second.ItemsUnchanged));
        Assert.Equal(SyncRunStatus.Succeeded, second.Status);
        Assert.NotNull(second.CompletedAtUtc);
        Assert.Equal(4, handler.Requests.Count);

        using var scope = factory.Services.CreateScope();
        Assert.Equal(3, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Products.CountAsync());
    }

    [Fact]
    public async Task ChangedProductIsMatchedByExternalIdAndUpdated()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(Json(new SupplierProductsPageDto([
            new("SUP-1", "NEW-SKU", "Updated", 12m, 7, false)], 1, 2, 1))));
        await using var factory = new TestWebApplicationFactory(handler);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Products.Add(new Product { ExternalId = "SUP-1", Sku = "OLD", Name = "Old", Price = 1,
                StockQuantity = 1, IsActive = true, CreatedAtUtc = DateTime.UtcNow.AddDays(-1),
                UpdatedAtUtc = DateTime.UtcNow.AddDays(-1), LastSyncedAtUtc = DateTime.UtcNow.AddDays(-1) });
            await db.SaveChangesAsync();
        }
        using var client = await AdminClientAsync(factory);

        var response = await client.PostAsync("/api/admin/integrations/supplier/sync", null);
        var run = await response.Content.ReadFromJsonAsync<SyncRunResponse>();

        Assert.Equal(1, run!.ItemsUpdated);
        using var verify = factory.Services.CreateScope();
        var product = await verify.ServiceProvider.GetRequiredService<AppDbContext>().Products.SingleAsync();
        Assert.Equal("NEW-SKU", product.Sku);
        Assert.Equal("SUP-1", product.ExternalId);
        Assert.True(product.UpdatedAtUtc > product.CreatedAtUtc);
    }

    [Fact]
    public async Task InvalidProductFailsSafelyAndPersistsFailedRun()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(Json(new SupplierProductsPageDto([
            new("SUP-1", "SKU", "Bad", -1m, -1, true)], 1, 2, 1))));
        await using var factory = new TestWebApplicationFactory(handler);
        using var client = await AdminClientAsync(factory);

        var response = await client.PostAsync("/api/admin/integrations/supplier/sync", null);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var run = await scope.ServiceProvider.GetRequiredService<AppDbContext>().SyncRuns.SingleAsync();
        Assert.Equal(SyncRunStatus.Failed, run.Status);
        Assert.Equal("invalid_supplier_product", run.FailureCode);
        Assert.NotNull(run.CompletedAtUtc);
        Assert.DoesNotContain("test-only-api-key", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TransientFailureIsRetriedButBadRequestIsNot()
    {
        var attempts = 0;
        var retryHandler = new RecordingHandler((_, _) => Task.FromResult(++attempts == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : Json(new SupplierProductsPageDto([], 1, 2, 1))));
        await using (var factory = new TestWebApplicationFactory(retryHandler))
        {
            using var client = await AdminClientAsync(factory);
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/admin/integrations/supplier/sync", null)).StatusCode);
            Assert.Equal(2, attempts);
        }

        attempts = 0;
        var badHandler = new RecordingHandler((_, _) =>
        {
            attempts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
        });
        await using (var factory = new TestWebApplicationFactory(badHandler))
        {
            using var client = await AdminClientAsync(factory);
            Assert.Equal(HttpStatusCode.BadGateway, (await client.PostAsync("/api/admin/integrations/supplier/sync", null)).StatusCode);
            Assert.Equal(1, attempts);
        }
    }

    [Fact]
    public async Task SupplierClientPropagatesCallerCancellation()
    {
        var handler = new RecordingHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Json(new SupplierProductsPageDto([], 1, 2, 1));
        });
        await using var factory = new TestWebApplicationFactory(handler);
        var supplierClient = factory.Services.GetRequiredService<SupplierIntegrationApi.Interfaces.ISupplierClient>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            supplierClient.GetProductsPageAsync(1, 2, cancellation.Token));
    }

    [Fact]
    public async Task OverlappingSyncReturnsConflictAndDatabaseIndexRejectsSecondRunningRun()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new RecordingHandler(async (_, cancellationToken) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return Json(new SupplierProductsPageDto([], 1, 2, 1));
        });
        await using var factory = new TestWebApplicationFactory(handler);
        using var firstClient = await AdminClientAsync(factory);
        using var secondClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        secondClient.DefaultRequestHeaders.Authorization = firstClient.DefaultRequestHeaders.Authorization;

        var first = firstClient.PostAsync("/api/admin/integrations/supplier/sync", null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await secondClient.PostAsync("/api/admin/integrations/supplier/sync", null);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        release.TrySetResult();
        Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.SyncRuns.Add(new SyncRun { Status = SyncRunStatus.Running, TriggerType = SyncTriggerType.Manual, StartedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        db.SyncRuns.Add(new SyncRun { Status = SyncRunStatus.Running, TriggerType = SyncTriggerType.Manual, StartedAtUtc = DateTime.UtcNow });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task ProductAndRunReadsAreProtectedPagedAndNewestFirst()
    {
        await using var factory = new TestWebApplicationFactory(new RecordingHandler((_, _) =>
            Task.FromResult(Json(new SupplierProductsPageDto([
                new("READ-1", "READ-SKU", "Readable", 5m, 2, true)], 1, 2, 1)))));
        using var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/products")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/admin/integrations/supplier/runs")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/admin/integrations/supplier/sync", null)).StatusCode);

        using var client = await AdminClientAsync(factory);
        await client.PostAsync("/api/admin/integrations/supplier/sync", null);
        await client.PostAsync("/api/admin/integrations/supplier/sync", null);
        var runs = await client.GetFromJsonAsync<PagedResponse<SyncRunResponse>>("/api/admin/integrations/supplier/runs?pageNumber=1&pageSize=1");
        Assert.Equal(2, runs!.TotalCount);
        Assert.Single(runs.Items);
        var products = await client.GetFromJsonAsync<PagedResponse<ProductResponse>>("/api/products?pageNumber=1&pageSize=1");
        Assert.Equal(1, products!.TotalCount);
        var detail = await client.GetFromJsonAsync<ProductResponse>($"/api/products/{products.Items[0].Id}");
        Assert.Equal("READ-1", detail!.ExternalId);
        var runDetail = await client.GetFromJsonAsync<SyncRunResponse>($"/api/admin/integrations/supplier/runs/{runs.Items[0].Id}");
        Assert.Equal(runs.Items[0].Id, runDetail!.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/admin/integrations/supplier/runs/99999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/products/99999")).StatusCode);
    }

    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    private static async Task<HttpClient> AdminClientAsync(TestWebApplicationFactory factory)
    {
        await factory.SeedAdminAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin@example.com", "Correct-Horse-Battery-Staple"));
        var auth = await login.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.AccessToken);
        return client;
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return response(request, cancellationToken);
        }
    }
}
