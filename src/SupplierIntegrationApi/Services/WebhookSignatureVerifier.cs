using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using SupplierIntegrationApi.Configuration;
using SupplierIntegrationApi.Interfaces;

namespace SupplierIntegrationApi.Services;

public sealed class WebhookSignatureVerifier(IOptions<SupplierOptions> options) : IWebhookSignatureVerifier
{
    private const string Prefix = "sha256=";
    private readonly byte[] key = Encoding.UTF8.GetBytes(options.Value.WebhookSecret);

    public bool IsValid(ReadOnlySpan<byte> body, string? suppliedSignature)
    {
        if (string.IsNullOrWhiteSpace(suppliedSignature) ||
            !suppliedSignature.StartsWith(Prefix, StringComparison.Ordinal) ||
            suppliedSignature.Length != Prefix.Length + 64)
        {
            return false;
        }

        byte[] suppliedDigest;
        try
        {
            suppliedDigest = Convert.FromHexString(suppliedSignature.AsSpan(Prefix.Length));
        }
        catch (FormatException) { return false; }
        if (suppliedDigest.Length != 32) return false;

        Span<byte> computedDigest = stackalloc byte[32];
        HMACSHA256.HashData(key, body, computedDigest);
        return CryptographicOperations.FixedTimeEquals(computedDigest, suppliedDigest);
    }
}
