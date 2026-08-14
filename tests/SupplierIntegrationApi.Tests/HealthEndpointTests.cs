using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SupplierIntegrationApi.Data;

namespace SupplierIntegrationApi.Tests;

public class HealthEndpointTests
{
    [Fact]
    public async Task HealthEndpointReturnsSuccess()
    {
        await using var factory = new TestWebApplicationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ReadinessReturnsSuccessWithRelationalDatabase()
    {
        await using var factory = new TestWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(TestWebApplicationFactory.JwtKey, await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(TestWebApplicationFactory.WebhookSecret, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task HealthEndpointsAreAnonymousAndDoNotCallSupplier()
    {
        var handler = new CountingHandler();
        await using var factory = new TestWebApplicationFactory(handler);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(0, handler.CallCount);

        using var scope = factory.Services.CreateScope();
        Assert.Empty(scope.ServiceProvider.GetRequiredService<AppDbContext>().SyncRuns);
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        private int callCount;
        public int CallCount => Volatile.Read(ref callCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref callCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }
}
