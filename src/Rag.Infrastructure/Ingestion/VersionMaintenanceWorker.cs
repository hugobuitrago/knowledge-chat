using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rag.Application.Ingestion;

namespace Rag.Infrastructure.Ingestion;

internal sealed class VersionMaintenanceWorker(
    IServiceScopeFactory serviceScopeFactory,
    IOptions<VersionMaintenanceOptions> options,
    ILogger<VersionMaintenanceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("Knowledge base version maintenance is disabled.");
            return;
        }

        using var timer = new PeriodicTimer(
            TimeSpan.FromMinutes(options.Value.IntervalMinutes));
        do
        {
            await RunOnceAsync(stoppingToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using AsyncServiceScope scope = serviceScopeFactory.CreateAsyncScope();
            IKnowledgeBaseVersionMaintenance maintenance = scope.ServiceProvider
                .GetRequiredService<IKnowledgeBaseVersionMaintenance>();
            int archived = await maintenance
                .ArchiveSupersededReadyVersionsAsync(cancellationToken)
                .ConfigureAwait(false);
            if (archived > 0)
            {
                logger.LogInformation(
                    "Archived superseded Ready knowledge base versions. Count={Count}",
                    archived);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "Knowledge base version maintenance failed. ErrorType={ErrorType}",
                exception.GetType().Name);
        }
    }
}
