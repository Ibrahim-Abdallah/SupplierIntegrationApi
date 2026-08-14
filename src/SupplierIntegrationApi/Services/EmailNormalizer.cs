using SupplierIntegrationApi.Interfaces;

namespace SupplierIntegrationApi.Services;

public sealed class EmailNormalizer : IEmailNormalizer
{
    public string Normalize(string email) => email.Trim().ToUpperInvariant();
}
