using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Rag.Application.Ingestion;
using Rag.Application.Providers;

namespace Rag.Infrastructure.Ingestion;

internal sealed class IngestionWorker(
    IServiceScopeFactory serviceScopeFactory,
    IOptions<IngestionWorkerOptions> options,
    ILogger<IngestionWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Task[] consumers = Enumerable
            .Range(0, options.Value.MaxConcurrentJobs)
            .Select(index => ConsumeAsync(index, stoppingToken))
            .ToArray();
        return Task.WhenAll(consumers);
    }

    private async Task ConsumeAsync(
        int consumerIndex,
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            bool acquired = await ConsumeOneAsync(
                consumerIndex,
                stoppingToken).ConfigureAwait(false);
            if (!acquired)
            {
                await Task.Delay(
                    options.Value.PollIntervalMilliseconds,
                    stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> ConsumeOneAsync(
        int consumerIndex,
        CancellationToken stoppingToken)
    {
        await using AsyncServiceScope scope = serviceScopeFactory.CreateAsyncScope();
        IIngestionJobQueue queue =
            scope.ServiceProvider.GetRequiredService<IIngestionJobQueue>();
        IngestionJobLease? lease = await queue
            .TryAcquireAsync(stoppingToken)
            .ConfigureAwait(false);
        if (lease is null)
        {
            return false;
        }

        IDocumentIngestionProcessor processor =
            scope.ServiceProvider.GetRequiredService<IDocumentIngestionProcessor>();
        try
        {
            await processor.ProcessAsync(lease, stoppingToken).ConfigureAwait(false);
            logger.LogInformation(
                "Ingestion job completed. JobId={JobId} TenantId={TenantId} Consumer={Consumer}",
                lease.JobId,
                lease.TenantId,
                consumerIndex);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (IngestionJobLeaseLostException)
        {
            logger.LogWarning(
                "Ingestion lease was lost. JobId={JobId} TenantId={TenantId} Consumer={Consumer}",
                lease.JobId,
                lease.TenantId,
                consumerIndex);
        }
        catch (Exception exception)
        {
            bool isTransient = IsTransient(exception);
            string safeError = GetSafeError(exception);
            logger.LogWarning(
                "Ingestion job failed. JobId={JobId} TenantId={TenantId} Consumer={Consumer} ErrorType={ErrorType} Transient={Transient}",
                lease.JobId,
                lease.TenantId,
                consumerIndex,
                exception.GetType().Name,
                isTransient);
            try
            {
                await queue
                    .FailAsync(lease, safeError, isTransient, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (IngestionJobLeaseLostException)
            {
                logger.LogWarning(
                    "Ingestion failure could not be recorded because the lease was lost. JobId={JobId} TenantId={TenantId}",
                    lease.JobId,
                    lease.TenantId);
            }
        }

        return true;
    }

    private static bool IsTransient(Exception exception) =>
        exception switch
        {
            EmbeddingProviderException providerException =>
                providerException.IsTransient,
            IngestionProcessingException processingException =>
                processingException.IsTransient,
            IOException => true,
            TimeoutException => true,
            NpgsqlException postgresException => postgresException.IsTransient,
            DbUpdateException { InnerException: NpgsqlException postgresException } =>
                postgresException.IsTransient,
            _ => false,
        };

    private static string GetSafeError(Exception exception) =>
        exception switch
        {
            EmbeddingProviderException => "The embedding provider failed.",
            IngestionProcessingException processingException =>
                processingException.Message,
            IOException => "The document storage operation failed.",
            TimeoutException => "The ingestion operation timed out.",
            NpgsqlException or DbUpdateException =>
                "The ingestion database operation failed.",
            _ => "The ingestion operation failed unexpectedly.",
        };
}
