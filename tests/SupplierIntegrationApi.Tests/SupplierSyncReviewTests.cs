using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using SupplierIntegrationApi.Data;
using SupplierIntegrationApi.DTOs;
using SupplierIntegrationApi.Entities;
using SupplierIntegrationApi.Enums;
using SupplierIntegrationApi.Interfaces;

namespace SupplierIntegrationApi.Tests;

public sealed class SupplierSyncReviewTests
{
    [Fact]
    public async Task SupplierTimeoutReturns504AndPersistsSafeFailedRun()
    {
        var handler = new StubHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        await using var factory = new TestWebApplicationFactory(handler);
        using var client = await AdminClientAsync(factory);

        var response = await client.PostAsync("/api/admin/integrations/supplier/sync", null);

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var run = await scope.ServiceProvider.GetRequiredService<AppDbContext>().SyncRuns.SingleAsync();
        Assert.Equal(SyncRunStatus.Failed, run.Status);
        Assert.Equal("supplier_timeout", run.FailureCode);
        Assert.NotNull(run.CompletedAtUtc);
    }

    [Fact]
    public async Task CallerCancellationPersistsCancelledRunWithoutProducts()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHandler(async (_, cancellationToken) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        await using var factory = new TestWebApplicationFactory(handler);
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ISupplierSyncService>();
        using var cancellation = new CancellationTokenSource();

        var sync = service.RunManualAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sync);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.ChangeTracker.Clear();
        var run = await db.SyncRuns.SingleAsync();
        Assert.Equal(SyncRunStatus.Cancelled, run.Status);
        Assert.Equal("cancelled", run.FailureCode);
        Assert.Empty(await db.Products.ToListAsync());
    }

    [Fact]
    public async Task AuthenticatedNonAdminCannotStartSync()
    {
        await using var factory = new TestWebApplicationFactory(new StubHandler((_, _) =>
            Task.FromResult(Json(new SupplierProductsPageDto([], 1, 2, 1)))));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken("Viewer"));

        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsync("/api/admin/integrations/supplier/sync", null)).StatusCode);
    }

    [Fact]
    public async Task DuplicateSupplierIdentifierFailsWithoutPersistingProducts()
    {
        var page = new SupplierProductsPageDto([
            new("DUP-1", "SKU-1", "One", 1, 1, true),
            new("DUP-1", "SKU-2", "Two", 2, 2, true)], 1, 2, 1);
        await AssertControlledPayloadFailureAsync(
            new StubHandler((_, _) => Task.FromResult(Json(page))), "duplicate_supplier_product");
    }

    [Fact]
    public async Task MalformedJsonFailsAsInvalidResponseWithoutPersistingProducts()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{ this is not valid json", Encoding.UTF8, "application/json")
        };
        await AssertControlledPayloadFailureAsync(
            new StubHandler((_, _) => Task.FromResult(response)), "supplier_invalid_response");
    }

    [Fact]
    public async Task LaterInvalidPagePreservesItemsReadButPersistsNoProducts()
    {
        var calls = 0;
        var handler = new StubHandler((_, _) => Task.FromResult(++calls == 1
            ? Json(new SupplierProductsPageDto([
                new("VALID-1", "SKU-1", "Valid", 1, 1, true)], 1, 2, 2))
            : Json(new SupplierProductsPageDto([], 99, 2, 2))));
        await using var factory = new TestWebApplicationFactory(handler);
        using var client = await AdminClientAsync(factory);

        Assert.Equal(HttpStatusCode.BadGateway,
            (await client.PostAsync("/api/admin/integrations/supplier/sync", null)).StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, (await db.SyncRuns.SingleAsync()).ItemsRead);
        Assert.Empty(await db.Products.ToListAsync());
    }

    [Fact]
    public async Task AbsurdPaginationMetadataIsRejectedWithoutRequestingMorePages()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Json(
            new SupplierProductsPageDto([], 1, 2, int.MaxValue))));
        await AssertControlledPayloadFailureAsync(handler, "supplier_invalid_response");
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task HistoryIsNewestFirstWithIdAsTieBreaker()
    {
        await using var factory = new TestWebApplicationFactory(new StubHandler((_, _) =>
            Task.FromResult(Json(new SupplierProductsPageDto([], 1, 2, 1)))));
        long oldId;
        long firstTieId;
        long secondTieId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var old = CompletedRun(DateTime.UtcNow.AddMinutes(-2));
            var firstTie = CompletedRun(DateTime.UtcNow.AddMinutes(-1));
            var secondTie = CompletedRun(firstTie.StartedAtUtc);
            db.SyncRuns.AddRange(old, firstTie, secondTie);
            await db.SaveChangesAsync();
            (oldId, firstTieId, secondTieId) = (old.Id, firstTie.Id, secondTie.Id);
        }
        using var client = await AdminClientAsync(factory);

        var history = await client.GetFromJsonAsync<PagedResponse<SyncRunResponse>>(
            "/api/admin/integrations/supplier/runs?pageNumber=1&pageSize=10");

        Assert.Equal([secondTieId, firstTieId, oldId], history!.Items.Select(run => run.Id));
    }

    private static SyncRun CompletedRun(DateTime started) => new()
    {
        TriggerType = SyncTriggerType.Manual,
        Status = SyncRunStatus.Succeeded,
        StartedAtUtc = started,
        CompletedAtUtc = started.AddSeconds(1)
    };

    private static async Task AssertControlledPayloadFailureAsync(StubHandler handler, string failureCode)
    {
        await using var factory = new TestWebApplicationFactory(handler);
        using var client = await AdminClientAsync(factory);
        var response = await client.PostAsync("/api/admin/integrations/supplier/sync", null);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(failureCode, (await db.SyncRuns.SingleAsync()).FailureCode);
        Assert.Empty(await db.Products.ToListAsync());
    }

    private static async Task<HttpClient> AdminClientAsync(TestWebApplicationFactory factory)
    {
        await factory.SeedAdminAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin@example.com", "Correct-Horse-Battery-Staple"));
        var auth = await login.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.AccessToken);
        return client;
    }

    private static string CreateToken(string role)
    {
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            TestWebApplicationFactory.JwtIssuer,
            TestWebApplicationFactory.JwtAudience,
            [new Claim(ClaimTypes.Role, role), new Claim(ClaimTypes.NameIdentifier, "999")],
            now,
            now.AddMinutes(5),
            new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestWebApplicationFactory.JwtKey)),
                SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response)
        : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return response(request, cancellationToken);
        }
    }
}
