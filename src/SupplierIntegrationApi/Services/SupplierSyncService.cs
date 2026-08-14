using FluentValidation;
using Microsoft.EntityFrameworkCore;
using SupplierIntegrationApi.Configuration;
using SupplierIntegrationApi.Data;
using SupplierIntegrationApi.DTOs;
using SupplierIntegrationApi.Entities;
using SupplierIntegrationApi.Enums;
using SupplierIntegrationApi.Interfaces;
using Microsoft.Extensions.Options;

namespace SupplierIntegrationApi.Services;

public sealed class SupplierSyncService(
    AppDbContext dbContext,
    ISupplierClient supplierClient,
    IValidator<SupplierProductDto> validator,
    IOptions<SupplierOptions> options,
    TimeProvider timeProvider,
    IServiceScopeFactory scopeFactory,
    ILogger<SupplierSyncService> logger) : ISupplierSyncService
{
    private const int MaxSupplierPages = 10_000;
    private const int MaxSupplierProducts = 1_000_000;

    public Task<SyncRunResponse> RunManualAsync(CancellationToken cancellationToken) =>
        RunAsync(SyncTriggerType.Manual, cancellationToken);

    public Task<SyncRunResponse> RunScheduledAsync(CancellationToken cancellationToken) =>
        RunAsync(SyncTriggerType.Scheduled, cancellationToken);

    private async Task<SyncRunResponse> RunAsync(
        SyncTriggerType triggerType, CancellationToken cancellationToken)
    {
        var run = new SyncRun
        {
            TriggerType = triggerType,
            Status = SyncRunStatus.Running,
            StartedAtUtc = timeProvider.GetUtcNow().UtcDateTime
        };

        dbContext.SyncRuns.Add(run);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsSingleRunningViolation(exception))
        {
            dbContext.Entry(run).State = EntityState.Detached;
            throw new SyncAlreadyRunningException();
        }

        logger.LogInformation("Supplier sync {SyncRunId} started with trigger {TriggerType}", run.Id, triggerType);
        try
        {
            var products = await ReadAllProductsAsync(run, cancellationToken);
            await UpsertAsync(products, run, cancellationToken);
            run.Status = SyncRunStatus.Succeeded;
            run.CompletedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Supplier sync {SyncRunId} completed: {Read} read, {Created} created, {Updated} updated, {Unchanged} unchanged",
                run.Id, run.ItemsRead, run.ItemsCreated, run.ItemsUpdated, run.ItemsUnchanged);
            return Map(run);
        }
        catch (OperationCanceledException)
        {
            await FinalizeFailureAsync(run, SyncRunStatus.Cancelled, "cancelled", "The synchronization was cancelled.");
            throw;
        }
        catch (SupplierException exception)
        {
            await FinalizeFailureAsync(run, SyncRunStatus.Failed, exception.Code, exception.SafeMessage);
            logger.LogWarning("Supplier sync {SyncRunId} failed with {FailureCode}", run.Id, exception.Code);
            throw;
        }
        catch (Exception exception)
        {
            await FinalizeFailureAsync(run, SyncRunStatus.Failed, "sync_failed", "The synchronization could not be completed.");
            logger.LogError("Supplier sync {SyncRunId} failed with unexpected category {FailureCategory}",
                run.Id, exception.GetType().Name);
            throw;
        }
    }

    private async Task<List<SupplierProductDto>> ReadAllProductsAsync(
        SyncRun run, CancellationToken cancellationToken)
    {
        var all = new List<SupplierProductDto>();
        var externalIds = new HashSet<string>(StringComparer.Ordinal);
        var page = 1;
        int? totalPages = null;

        while (totalPages is null || page <= totalPages)
        {
            var response = await supplierClient.GetProductsPageAsync(page, options.Value.PageSize, cancellationToken);
            if (response.Page != page || response.PageSize != options.Value.PageSize || response.TotalPages < 1
                || response.TotalPages > MaxSupplierPages || response.TotalPages < response.Page
                || (long)response.TotalPages * response.PageSize > MaxSupplierProducts)
                throw new SupplierException("supplier_invalid_response", "The supplier returned invalid pagination metadata.");
            if (totalPages is not null && response.TotalPages != totalPages)
                throw new SupplierException("supplier_invalid_response", "The supplier returned inconsistent pagination metadata.");
            totalPages = response.TotalPages;

            foreach (var product in response.Items ?? [])
            {
                var validation = await validator.ValidateAsync(product, cancellationToken);
                if (!validation.IsValid)
                    throw new SupplierException("invalid_supplier_product", "The supplier returned an invalid product.");
                if (!externalIds.Add(product.Id))
                    throw new SupplierException("duplicate_supplier_product", "The supplier returned a duplicate product identifier.");
                all.Add(product);
                run.ItemsRead++;
                if (all.Count > MaxSupplierProducts)
                    throw new SupplierException("supplier_invalid_response", "The supplier response exceeds safe synchronization limits.");
            }
            page++;
        }
        return all;
    }

    private async Task UpsertAsync(List<SupplierProductDto> incoming, SyncRun run, CancellationToken cancellationToken)
    {
        var ids = incoming.Select(item => item.Id).ToList();
        var existing = await dbContext.Products.Where(product => ids.Contains(product.ExternalId))
            .ToDictionaryAsync(product => product.ExternalId, cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        foreach (var item in incoming)
        {
            if (!existing.TryGetValue(item.Id, out var product))
            {
                dbContext.Products.Add(new Product
                {
                    ExternalId = item.Id, Sku = item.Sku, Name = item.Name, Price = item.Price,
                    StockQuantity = item.StockQuantity, IsActive = item.IsActive,
                    CreatedAtUtc = now, UpdatedAtUtc = now, LastSyncedAtUtc = now
                });
                run.ItemsCreated++;
                continue;
            }

            var changed = product.Sku != item.Sku || product.Name != item.Name || product.Price != item.Price
                || product.StockQuantity != item.StockQuantity || product.IsActive != item.IsActive;
            product.LastSyncedAtUtc = now;
            if (changed)
            {
                product.Sku = item.Sku; product.Name = item.Name; product.Price = item.Price;
                product.StockQuantity = item.StockQuantity; product.IsActive = item.IsActive; product.UpdatedAtUtc = now;
                run.ItemsUpdated++;
            }
            else run.ItemsUnchanged++;
        }
    }

    private async Task FinalizeFailureAsync(SyncRun run, SyncRunStatus status, string code, string message)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var finalizationContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var persistedRun = await finalizationContext.SyncRuns.SingleAsync(item => item.Id == run.Id, CancellationToken.None);
            persistedRun.Status = status;
            persistedRun.CompletedAtUtc = timeProvider.GetUtcNow().UtcDateTime;
            persistedRun.ItemsRead = run.ItemsRead;
            persistedRun.ItemsCreated = run.ItemsCreated;
            persistedRun.ItemsUpdated = run.ItemsUpdated;
            persistedRun.ItemsUnchanged = run.ItemsUnchanged;
            persistedRun.FailureCode = code;
            persistedRun.FailureMessage = message;
            await finalizationContext.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError("Could not finalize supplier sync {SyncRunId}; failure category {FailureCategory}",
                run.Id, exception.GetType().Name);
        }
    }

    private static bool IsSingleRunningViolation(DbUpdateException exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current.GetType().FullName == "Microsoft.Data.SqlClient.SqlException")
            {
                var number = current.GetType().GetProperty("Number")?.GetValue(current) as int?;
                if (number is 2601 or 2627
                    && current.Message.Contains("UX_SyncRuns_OneRunning", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            if (current.GetType().FullName == "Microsoft.Data.Sqlite.SqliteException"
                && current.Message.Contains("UNIQUE constraint failed: SyncRuns.Status", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public static SyncRunResponse Map(SyncRun run) => new(run.Id, run.TriggerType, run.Status, run.StartedAtUtc,
        run.CompletedAtUtc, run.ItemsRead, run.ItemsCreated, run.ItemsUpdated, run.ItemsUnchanged,
        run.FailureCode, run.FailureMessage);
}
