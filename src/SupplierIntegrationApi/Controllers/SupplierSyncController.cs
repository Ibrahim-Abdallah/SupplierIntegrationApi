using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SupplierIntegrationApi.Data;
using SupplierIntegrationApi.DTOs;
using SupplierIntegrationApi.Enums;
using SupplierIntegrationApi.Interfaces;
using SupplierIntegrationApi.Services;

namespace SupplierIntegrationApi.Controllers;

[ApiController]
[Route("api/admin/integrations/supplier")]
[Authorize(Roles = "Admin")]
public sealed class SupplierSyncController(ISupplierSyncService syncService, AppDbContext dbContext) : ControllerBase
{
    [HttpPost("sync")]
    public async Task<ActionResult<SyncRunResponse>> Start(CancellationToken cancellationToken)
    {
        try { return Ok(await syncService.RunManualAsync(cancellationToken)); }
        catch (SyncAlreadyRunningException exception) { return Problem(title: exception.Message, statusCode: 409); }
        catch (SupplierException exception)
        {
            var status = exception.Code == "supplier_timeout" ? 504 : 502;
            return Problem(title: "Supplier synchronization failed", detail: exception.SafeMessage, statusCode: status);
        }
    }

    [HttpGet("runs")]
    public async Task<ActionResult<PagedResponse<SyncRunResponse>>> Runs(
        [FromQuery] int pageNumber = 1, [FromQuery] int pageSize = 20,
        [FromQuery] SyncRunStatus? status = null, [FromQuery] SyncTriggerType? triggerType = null,
        CancellationToken cancellationToken = default)
    {
        if (pageNumber < 1 || pageSize is < 1 or > 100) return ValidationProblem();
        var query = dbContext.SyncRuns.AsNoTracking().AsQueryable();
        if (status.HasValue) query = query.Where(run => run.Status == status);
        if (triggerType.HasValue) query = query.Where(run => run.TriggerType == triggerType);
        var count = await query.CountAsync(cancellationToken);
        var runs = await query.OrderByDescending(run => run.StartedAtUtc).ThenByDescending(run => run.Id)
            .Skip((pageNumber - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        var items = runs.Select(SupplierSyncService.Map).ToList();
        return Ok(new PagedResponse<SyncRunResponse>(items, pageNumber, pageSize, count));
    }

    [HttpGet("runs/{id:long}")]
    public async Task<ActionResult<SyncRunResponse>> Run(long id, CancellationToken cancellationToken)
    {
        var run = await dbContext.SyncRuns.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        return run is null ? NotFound() : Ok(SupplierSyncService.Map(run));
    }
}
