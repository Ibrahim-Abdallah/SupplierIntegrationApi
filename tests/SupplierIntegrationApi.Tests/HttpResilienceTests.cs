using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupplierIntegrationApi.Data;
using SupplierIntegrationApi.DTOs;
using SupplierIntegrationApi.Enums;

namespace SupplierIntegrationApi.Tests;

public sealed class HttpResilienceTests
{
    private const int MaxAttempts = 4;

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public async Task TransientServerFailureThenSuccessRetriesAndCompletes(HttpStatusCode transientStatus)
    {
        var handler = new ControlledHandler((attempt, _, _) => Task.FromResult(attempt == 1
            ? new HttpResponseMessage(transientStatus)
            : Success()));
        await using var factory = new TestWebApplicationFactory(handler);
        using var client = await AdminClientAsync(factory);

        var response = await client.PostAsync("/api/admin/integrations/supplier/sync", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.Attempts);
        await AssertRunAsync(factory, SyncRunStatus.Succeeded, null);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task RepeatedTransientServerFailureExhaustsFiniteBudget(HttpStatusCode status)
    {
        var handler = new ControlledHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(status)));
        await using var factory = new TestWebApplicationFactory(handler);
        using var client = await AdminClientAsync(factory);

        var response = await client.PostAsync("/api/admin/integrations/supplier/sync", null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(MaxAttempts, handler.Attempts);
        await AssertRunAsync(factory, SyncRunStatus.Failed, "supplier_unavailable");
    }

    [Fact]
    public async Task RateLimitThenSuccessRetriesAndCompletes()
    {
        var handler = new ControlledHandler((attempt, _, _) => Task.FromResult(attempt == 1
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            : Success()));
        await using var factory = new TestWebApplicationFactory(handler);
        using var client = await AdminClientAsync(factory);

        var response = await client.PostAsync("/api/admin/integrations/supplier/sync", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.Attempts);
    }

    [Fact]
    public async Task RepeatedRateLimitExhaustsFiniteBudgetAndFinalizesRun()
    {
        var handler = new ControlledHandler((_, _, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)));
        await using var factory = new TestWebApplicationFactory(handler);
        using var client = await AdminClientAsync(factory);

        var response = await client.PostAsync("/api/admin/integrations/supplier/sync", null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(MaxAttempts, handler.Attempts);
        await AssertRunAsync(factory, SyncRunStatus.Failed, "supplier_rate_limited");
    }

    [Fact]
    public async Task ValidRetryAfterDelayIsHonored()
    {
        var handler = new ControlledHandler((attempt, _, _) =>
        {
            if (attempt > 1) return Task.FromResult(Success());
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
            return Task.FromResult(response);
        });
        await using var factory = new TestWebApplicationFactory(handler);
        using var client = await AdminClientAsync(factory);
        var stopwatch = Stopwatch.StartNew();

        var response = await client.PostAsync("/api/admin/integrations/supplier/sync", null);

        stopwatch.Stop();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.Attempts);
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(800), $"Elapsed: {stopwatch.Elapsed}");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Elapsed: {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task InvalidRetryAfterFallsBackToBoundedConfiguredDelay()
    {
        var handler = new ControlledHandler((attempt, _, _) =>
        {
            if (attempt > 1) return Task.FromResult(Success());
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.TryAddWithoutValidation("Retry-After", "invalid");
            return Task.FromResult(response);
        });
        await using var factory = new TestWebApplicationFactory(handler);
        using var client = await AdminClientAsync(factory);
        var stopwatch = Stopwatch.StartNew();

        var response = await client.PostAsync("/api/admin/integrations/supplier/sync", null);

        stopwatch.Stop();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.Attempts);
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(150), $"Elapsed: {stopwatch.Elapsed}");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Elapsed: {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task UnreasonableRetryAfterIsBoundedByTotalTimeout()
    {
        var handler = new ControlledHandler((_, _, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(10));
            return Task.FromResult(response);
        });
        await using var factory = new TestWebApplicationFactory(handler);
        using var client = await AdminClientAsync(factory);
        var stopwatch = Stopwatch.StartNew();

        var response = await client.PostAsync("/api/admin/integrations/supplier/sync", null);

        stopwatch.Stop();
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"Elapsed: {stopwatch.Elapsed}");
        await AssertRunAsync(factory, SyncRunStatus.Failed, "supplier_timeout");
    }

    [Fact]
    public async Task RepeatedRequestTimeoutResponseIsRetriedAndMapsToGatewayTimeout()
    {
        var handler = new ControlledHandler((_, _, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestTimeout)));
        await using var factory = new TestWebApplicationFactory(handler);
        using var client = await AdminClientAsync(factory);

        var response = await client.PostAsync("/api/admin/integrations/supplier/sync", null);

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal(MaxAttempts, handler.Attempts);
        await AssertRunAsync(factory, SyncRunStatus.Failed, "supplier_timeout");
    }

    [Fact]
    public async Task RepeatedConnectionFailureIsRetriedAndMapsToUnavailable()
    {
        var handler = new ControlledHandler((_, _, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("test transport failure")));
        await using var factory = new TestWebApplicationFactory(handler);
        using var client = await AdminClientAsync(factory);

        var response = await client.PostAsync("/api/admin/integrations/supplier/sync", null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(MaxAttempts, handler.Attempts);
        await AssertRunAsync(factory, SyncRunStatus.Failed, "supplier_unavailable");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "supplier_rejected")]
    [InlineData(HttpStatusCode.Unauthorized, "supplier_unauthorized")]
    [InlineData(HttpStatusCode.Forbidden, "supplier_unauthorized")]
    [InlineData(HttpStatusCode.NotFound, "supplier_rejected")]
    public async Task NonTransientClientErrorsAreNotRetried(HttpStatusCode status, string expectedCode)
    {
        var handler = new ControlledHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(status)));
        await using var factory = new TestWebApplicationFactory(handler);
        using var client = await AdminClientAsync(factory);

        var response = await client.PostAsync("/api/admin/integrations/supplier/sync", null);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(1, handler.Attempts);
        await AssertRunAsync(factory, SyncRunStatus.Failed, expectedCode);
    }

    [Fact]
    public async Task AttemptTimeoutUsesResiliencePipelineAndIsBounded()
    {
        var handler = new ControlledHandler(async (_, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Success();
        });
        await using var factory = new TestWebApplicationFactory(handler);
        using var client = await AdminClientAsync(factory);
        var stopwatch = Stopwatch.StartNew();

        var response = await client.PostAsync("/api/admin/integrations/supplier/sync", null);

        stopwatch.Stop();
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal(MaxAttempts, handler.Attempts);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Elapsed: {stopwatch.Elapsed}");
        await AssertRunAsync(factory, SyncRunStatus.Failed, "supplier_timeout");
    }

    [Fact]
    public async Task ProviderBodyAndApiKeyAreNotExposedInProblemDetails()
    {
        const string providerSecret = "provider-raw-secret-body";
        var handler = new ControlledHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(providerSecret)
        }));
        await using var factory = new TestWebApplicationFactory(handler);
        using var client = await AdminClientAsync(factory);

        var response = await client.PostAsync("/api/admin/integrations/supplier/sync", null);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.DoesNotContain(providerSecret, body, StringComparison.Ordinal);
        Assert.DoesNotContain("test-only-api-key", body, StringComparison.Ordinal);
        Assert.DoesNotContain("X-Api-Key", body, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task AssertRunAsync(
        TestWebApplicationFactory factory, SyncRunStatus status, string? failureCode)
    {
        using var scope = factory.Services.CreateScope();
        var runs = await scope.ServiceProvider.GetRequiredService<AppDbContext>().SyncRuns.AsNoTracking().ToListAsync();
        var run = Assert.Single(runs);
        Assert.Equal(status, run.Status);
        Assert.Equal(failureCode, run.FailureCode);
        Assert.NotEqual(SyncRunStatus.Running, run.Status);
        Assert.NotNull(run.CompletedAtUtc);
    }

    private static HttpResponseMessage Success() => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new SupplierProductsPageDto([], 1, 2, 1))
    };

    private static async Task<HttpClient> AdminClientAsync(TestWebApplicationFactory factory)
    {
        await factory.SeedAdminAsync();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var login = await client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("admin@example.com", "Correct-Horse-Battery-Staple"));
        var auth = await login.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.AccessToken);
        return client;
    }

    private sealed class ControlledHandler(
        Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        private int attempts;
        public int Attempts => Volatile.Read(ref attempts);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            response(Interlocked.Increment(ref attempts), request, cancellationToken);
    }
}
