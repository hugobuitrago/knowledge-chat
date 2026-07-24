using Rag.Infrastructure.Ingestion;
using Rag.Infrastructure.Observability;
using Rag.Infrastructure.Persistence;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.AddRagObservability("Rag.Worker");
builder.Services.AddRagPersistence(builder.Configuration);
builder.Services.AddRagIngestion(builder.Configuration, builder.Environment);
builder.Services.AddRagIngestionWorker(builder.Configuration);

IHost host = builder.Build();
host.Run();
