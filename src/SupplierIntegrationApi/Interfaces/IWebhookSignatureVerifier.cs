namespace SupplierIntegrationApi.Interfaces;

public interface IWebhookSignatureVerifier
{
    bool IsValid(ReadOnlySpan<byte> body, string? suppliedSignature);
}
