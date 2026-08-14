using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.Tokens;

namespace SupplierIntegrationApi.Tests;

public class JwtValidationTests
{
    [Theory]
    [InlineData("wrong-signature")]
    [InlineData("expired")]
    [InlineData("wrong-issuer")]
    [InlineData("wrong-audience")]
    public async Task InvalidJwtIsRejected(string caseName)
    {
        await using var factory = new TestWebApplicationFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(caseName));

        var response = await client.GetAsync("/api/admin/auth-check");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static string CreateToken(string caseName)
    {
        var now = DateTime.UtcNow;
        var key = caseName == "wrong-signature"
            ? "different-test-signing-key-with-at-least-32-bytes"
            : TestWebApplicationFactory.JwtKey;
        var token = new JwtSecurityToken(
            caseName == "wrong-issuer" ? "wrong-issuer" : TestWebApplicationFactory.JwtIssuer,
            caseName == "wrong-audience" ? "wrong-audience" : TestWebApplicationFactory.JwtAudience,
            [new Claim(ClaimTypes.Role, "Admin"), new Claim(ClaimTypes.NameIdentifier, "1")],
            caseName == "expired" ? now.AddMinutes(-10) : now,
            caseName == "expired" ? now.AddMinutes(-5) : now.AddMinutes(5),
            new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
                SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
