using Microsoft.Extensions.Options;
using SupplierIntegrationApi.Configuration;
using SupplierIntegrationApi.Interfaces;

namespace SupplierIntegrationApi.Services;

public sealed class SupplierSyncBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<SupplierOptions> options,
    ILogger<SupplierSyncBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.ScheduledSyncEnabled)
        {
            logger.LogInformation("Scheduled supplier synchronization is disabled");
            return;
        }

        var interval = TimeSpan.FromMinutes(settings.ScheduledSyncIntervalMinutes);
        logger.LogInformation(
            "Scheduled supplier synchronization is enabled with interval {IntervalMinutes} minutes",
            settings.ScheduledSyncIntervalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken);
            await Task.Delay(interval, stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var syncService = scope.ServiceProvider.GetRequiredService<ISupplierSyncService>();
            var result = await syncService.RunScheduledAsync(stoppingToken);
            logger.LogInformation("Scheduled supplier sync {SyncRunId} completed", result.Id);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SyncAlreadyRunningException)
        {
            logger.LogInformation("Scheduled supplier synchronization skipped because another sync is running");
        }
        catch (SupplierException exception)
        {
            logger.LogWarning(
                "Scheduled supplier synchronization failed with {FailureCode}; the next run remains scheduled",
                exception.Code);
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Scheduled supplier synchronization failed with unexpected category {FailureCategory}; the next run remains scheduled",
                exception.GetType().Name);
        }
    }
}
