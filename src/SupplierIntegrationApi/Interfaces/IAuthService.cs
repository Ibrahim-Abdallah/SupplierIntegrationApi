using SupplierIntegrationApi.DTOs;

namespace SupplierIntegrationApi.Interfaces;

public interface IAuthService
{
    Task<AuthResponse?> LoginAsync(LoginRequest request, CancellationToken cancellationToken);
}
