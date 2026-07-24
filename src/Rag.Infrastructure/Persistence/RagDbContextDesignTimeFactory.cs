using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using Rag.Application.Abstractions;

namespace Rag.Infrastructure.Persistence;

public sealed class RagDbContextDesignTimeFactory : IDesignTimeDbContextFactory<RagDbContext>
{
    private const string LocalDevelopmentConnectionString =
        "Host=localhost;Port=5432;Database=rag;Username=rag;Password=rag_dev_only";

    public RagDbContext CreateDbContext(string[] args)
    {
        string connectionString = Environment.GetEnvironmentVariable("Database__ConnectionString")
            ?? LocalDevelopmentConnectionString;
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        dataSourceBuilder.UseVector();
        NpgsqlDataSource dataSource = dataSourceBuilder.Build();

        var optionsBuilder = new DbContextOptionsBuilder<RagDbContext>();
        optionsBuilder
            .UseNpgsql(dataSource, options => options.UseVector())
            .AddInterceptors(
                new UtcAuditableEntityInterceptor(new DesignTimeClock()),
                new PersistenceInvariantInterceptor());

        return new RagDbContext(optionsBuilder.Options);
    }

    private sealed class DesignTimeClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}

