using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SupplierIntegrationApi.Data;
using SupplierIntegrationApi.DTOs;

namespace SupplierIntegrationApi.Controllers;

[ApiController]
[Route("api/products")]
[Authorize(Roles = "Admin")]
public sealed class ProductsController(AppDbContext dbContext) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResponse<ProductResponse>>> List(
        [FromQuery] int pageNumber = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default)
    {
        if (pageNumber < 1 || pageSize is < 1 or > 100) return ValidationProblem();
        var query = dbContext.Products.AsNoTracking();
        var count = await query.CountAsync(cancellationToken);
        var products = await query.OrderBy(product => product.ExternalId).ThenBy(product => product.Id)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        var items = products.Select(Map).ToList();
        return Ok(new PagedResponse<ProductResponse>(items, pageNumber, pageSize, count));
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ProductResponse>> Detail(int id, CancellationToken cancellationToken)
    {
        var product = await dbContext.Products.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        return product is null ? NotFound() : Ok(Map(product));
    }

    private static ProductResponse Map(Entities.Product product) => new(product.Id, product.ExternalId,
        product.Sku, product.Name, product.Price, product.StockQuantity, product.IsActive,
        product.LastSyncedAtUtc, product.CreatedAtUtc, product.UpdatedAtUtc);
}
