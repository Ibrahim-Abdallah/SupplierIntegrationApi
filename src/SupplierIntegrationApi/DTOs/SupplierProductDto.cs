namespace SupplierIntegrationApi.DTOs;

public sealed record SupplierProductDto(
    string Id,
    string Sku,
    string Name,
    decimal Price,
    int StockQuantity,
    bool IsActive);

public sealed record SupplierProductsPageDto(
    IReadOnlyList<SupplierProductDto> Items,
    int Page,
    int PageSize,
    int TotalPages);
