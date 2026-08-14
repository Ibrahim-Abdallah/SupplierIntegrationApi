using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

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
}
