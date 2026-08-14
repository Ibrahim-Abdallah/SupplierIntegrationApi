using SupplierIntegrationApi.DTOs;
using SupplierIntegrationApi.Entities;

namespace SupplierIntegrationApi.Interfaces;

public interface IJwtTokenService
{
    AuthResponse CreateAccessToken(User user);
}
