using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SupplierIntegrationApi.Data;
using SupplierIntegrationApi.Entities;
using SupplierIntegrationApi.Enums;
using SupplierIntegrationApi.Interfaces;
using SupplierIntegrationApi.Services;

namespace SupplierIntegrationApi.Tests;

public sealed class ScheduledSyncTests
{
    [Fact]
    public async Task DisabledSchedulingDoesNotCallSupplierOrCreateRun()
    {
        var handler = new CoordinatedHandler(_ => SuccessResponse());
        await using var factory = new TestWebApplicationFactory(handler);
        _ = factory.CreateClient();

        await Task.Delay(100);

        Assert.Equal(0, handler.CallCount);
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<AppDbContext>().SyncRuns.ToListAsync());
    }

    [Fact]
    public async Task EnabledSchedulingImmediatelyRunsSharedProductWorkflow()
    {
        var handler = new CoordinatedHandler(_ => SuccessResponse());
        await using var factory = new TestWebApplicationFactory(handler, scheduledSyncEnabled: true);
        _ = factory.CreateClient();

        await handler.FirstCall.WaitAsync(TimeSpan.FromSeconds(5));
        var run = await WaitForRunAsync(factory, SyncRunStatus.Succeeded);

        Assert.Equal(SyncTriggerType.Scheduled, run.TriggerType);
        Assert.Equal(1, run.ItemsCreated);
        await using var scope = factory.Services.CreateAsyncScope();
        var product = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Products.SingleAsync();
        Assert.Equal("SUP-SCHEDULED", product.ExternalId);
    }

    [Fact]
    public async Task ScheduledServiceUsesDatabaseBackedOverlapRule()
    {
        await using var factory = new TestWebApplicationFactory(new CoordinatedHandler(_ => SuccessResponse()));
        _ = factory.CreateClient();
        await using (var setupScope = factory.Services.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.SyncRuns.Add(new SyncRun
            {
                TriggerType = SyncTriggerType.Manual,
                Status = SyncRunStatus.Running,
                StartedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ISupplierSyncService>();
        await Assert.ThrowsAsync<SyncAlreadyRunningException>(() => service.RunScheduledAsync(CancellationToken.None));
    }

    [Fact]
    public async Task FailedScheduledRunIsFinalizedAndHostRemainsHealthy()
    {
        var handler = new CoordinatedHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));
        await using var factory = new TestWebApplicationFactory(handler, scheduledSyncEnabled: true);
        using var client = factory.CreateClient();

        await handler.FirstCall.WaitAsync(TimeSpan.FromSeconds(5));
        var run = await WaitForRunAsync(factory, SyncRunStatus.Failed);

        Assert.Equal(SyncTriggerType.Scheduled, run.TriggerType);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    private static async Task<SyncRun> WaitForRunAsync(TestWebApplicationFactory factory, SyncRunStatus status)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var run = await scope.ServiceProvider.GetRequiredService<AppDbContext>().SyncRuns
                .AsNoTracking().SingleOrDefaultAsync(item => item.Status == status, timeout.Token);
            if (run is not null) return run;
            await Task.Delay(20, timeout.Token);
        }
    }

    private static HttpResponseMessage SuccessResponse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            """{"items":[{"id":"SUP-SCHEDULED","sku":"SCH-1","name":"Scheduled product","price":12.50,"stockQuantity":4,"isActive":true}],"page":1,"pageSize":2,"totalPages":1}""",
            Encoding.UTF8,
            "application/json")
    };

    private sealed class CoordinatedHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        private readonly TaskCompletionSource firstCall = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int callCount;
        public int CallCount => Volatile.Read(ref callCount);
        public Task FirstCall => firstCall.Task;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref callCount);
            firstCall.TrySetResult();
            return Task.FromResult(response(request));
        }
    }
}
