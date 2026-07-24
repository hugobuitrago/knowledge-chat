using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using Rag.Application.Abstractions;

namespace Rag.Infrastructure.Persistence;

public static class PersistenceExtensions
{
    public static IServiceCollection AddRagPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                static options => IsValidConnectionString(options.ConnectionString),
                $"{DatabaseOptions.SectionName}:ConnectionString must be a valid PostgreSQL connection string.")
            .ValidateOnStart();

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<UtcAuditableEntityInterceptor>();
        services.AddSingleton<PersistenceInvariantInterceptor>();
        services.AddSingleton(static serviceProvider =>
        {
            DatabaseOptions options = serviceProvider
                .GetRequiredService<IOptions<DatabaseOptions>>()
                .Value;
            var dataSourceBuilder = new NpgsqlDataSourceBuilder(options.ConnectionString);
            dataSourceBuilder.UseVector();
            return dataSourceBuilder.Build();
        });

        services.AddDbContextPool<RagDbContext>((serviceProvider, dbContextOptions) =>
        {
            DatabaseOptions options = serviceProvider
                .GetRequiredService<IOptions<DatabaseOptions>>()
                .Value;
            dbContextOptions
                .UseNpgsql(
                    serviceProvider.GetRequiredService<NpgsqlDataSource>(),
                    npgsqlOptions =>
                    {
                        npgsqlOptions.UseVector();
                        npgsqlOptions.CommandTimeout(options.CommandTimeoutSeconds);
                    })
                .AddInterceptors(
                    serviceProvider.GetRequiredService<UtcAuditableEntityInterceptor>(),
                    serviceProvider.GetRequiredService<PersistenceInvariantInterceptor>());
        });

        services
            .AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>(
                "postgresql",
                failureStatus: Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy,
                tags: ["ready", "dependencies"]);

        return services;
    }

    private static bool IsValidConnectionString(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return false;
        }

        try
        {
            _ = new NpgsqlConnectionStringBuilder(connectionString);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

