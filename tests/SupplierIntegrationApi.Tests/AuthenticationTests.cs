using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupplierIntegrationApi.Data;
using SupplierIntegrationApi.DTOs;

namespace SupplierIntegrationApi.Tests;

public class AuthenticationTests
{
    [Fact]
    public async Task ValidAdminLoginReturnsJwtAndStoredPasswordIsHashed()
    {
        await using var factory = new TestWebApplicationFactory();
        const string password = "Correct-Horse-Battery-Staple";
        await factory.SeedAdminAsync(password: password);
        using var client = CreateClient(factory);

        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin@example.com", password));
        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(auth);
        Assert.Equal("Bearer", auth.TokenType);
        Assert.False(string.IsNullOrWhiteSpace(auth.AccessToken));
        Assert.True(auth.ExpiresAtUtc > DateTime.UtcNow);

        using var scope = factory.Services.CreateScope();
        var storedHash = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .Users.Select(user => user.PasswordHash).SingleAsync();
        Assert.NotEqual(password, storedHash);
        Assert.DoesNotContain(password, storedHash, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("admin@example.com", "wrong-password")]
    [InlineData("unknown@example.com", "Correct-Horse-Battery-Staple")]
    public async Task InvalidCredentialsReturnUnauthorized(string email, string password)
    {
        await using var factory = new TestWebApplicationFactory();
        await factory.SeedAdminAsync();
        using var client = CreateClient(factory);

        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task InactiveAdminReturnsUnauthorized()
    {
        await using var factory = new TestWebApplicationFactory();
        await factory.SeedAdminAsync(isActive: false);
        using var client = CreateClient(factory);

        var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest("admin@example.com", "Correct-Horse-Battery-Staple"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task LoginNormalizesEmailInvariantly()
    {
        await using var factory = new TestWebApplicationFactory();
        await factory.SeedAdminAsync("Admin@Example.com");
        using var client = CreateClient(factory);

        var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest("  aDmIn@eXaMpLe.CoM  ", "Correct-Horse-Battery-Staple"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("", "password")]
    [InlineData("not-an-email", "password")]
    [InlineData("admin@example.com", "")]
    public async Task InvalidLoginRequestReturnsValidationProblem(string email, string password)
    {
        await using var factory = new TestWebApplicationFactory();
        using var client = CreateClient(factory);

        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("errors", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnonymousAdminEndpointReturnsUnauthorized()
    {
        await using var factory = new TestWebApplicationFactory();
        using var client = CreateClient(factory);

        var response = await client.GetAsync("/api/admin/auth-check");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ValidAdminJwtCanAccessAdminEndpoint()
    {
        await using var factory = new TestWebApplicationFactory();
        await factory.SeedAdminAsync();
        using var client = CreateClient(factory);
        var login = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest("admin@example.com", "Correct-Horse-Battery-Staple"));
        var auth = await login.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.AccessToken);

        var response = await client.GetAsync("/api/admin/auth-check");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task OpenApiMarksOnlyAdminEndpointWithBearerSecurity()
    {
        await using var factory = new TestWebApplicationFactory();
        using var client = CreateClient(factory);

        var document = await client.GetFromJsonAsync<JsonElement>("/openapi/v1.json");
        var schemes = document.GetProperty("components").GetProperty("securitySchemes");
        var login = document.GetProperty("paths").GetProperty("/api/auth/login").GetProperty("post");
        var admin = document.GetProperty("paths").GetProperty("/api/admin/auth-check").GetProperty("get");
        var sync = document.GetProperty("paths").GetProperty("/api/admin/integrations/supplier/sync").GetProperty("post");
        var runs = document.GetProperty("paths").GetProperty("/api/admin/integrations/supplier/runs").GetProperty("get");
        var products = document.GetProperty("paths").GetProperty("/api/products").GetProperty("get");

        Assert.True(schemes.TryGetProperty("Bearer", out _));
        Assert.False(login.TryGetProperty("security", out _));
        Assert.True(admin.GetProperty("security").GetArrayLength() > 0);
        Assert.True(sync.GetProperty("security").GetArrayLength() > 0);
        Assert.True(runs.GetProperty("security").GetArrayLength() > 0);
        Assert.True(products.GetProperty("security").GetArrayLength() > 0);
    }

    private static HttpClient CreateClient(TestWebApplicationFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
}
