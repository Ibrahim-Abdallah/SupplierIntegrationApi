namespace SupplierIntegrationApi.DTOs;

public sealed record AuthResponse(string AccessToken, string TokenType, DateTime ExpiresAtUtc);
