# Rag

Fundação de uma API RAG multi-tenant em .NET 10. O repositório contém um monólito modular com API e Worker implantáveis separadamente. Esta entrega implementa as Fases 0 a 5: fundação arquitetural, persistência PostgreSQL/pgvector, segurança máquina a máquina, upload streaming, fila com leases, chunking e embeddings. Ativação blue/green, retrieval e geração ainda não foram implementados.

## Pré-requisitos

- .NET SDK 10.0.302 ou feature band compatível, conforme `global.json`;
- Docker Desktop ou outro engine compatível com Docker Compose.

Os pacotes são restaurados exclusivamente do feed público definido em `NuGet.config`; versões são centralizadas em `Directory.Packages.props` e auditadas pelo restore.

## Preparar o ambiente local

Restaure as ferramentas e dependências, inicie o PostgreSQL de desenvolvimento e aplique a migração:

```powershell
dotnet tool restore
dotnet restore Rag.sln
docker compose up -d postgres
dotnet ef database update --project src/Rag.Infrastructure --startup-project src/Rag.Api
```

O Compose usa `pgvector/pgvector:0.8.5-pg18-bookworm`, publica a porta `5432` e mantém os dados em volume Docker. A senha `rag_dev_only` existe somente no ambiente local versionado e não deve ser reutilizada fora dele.

Para encerrar o container sem remover o volume:

```powershell
docker compose stop postgres
```

## Validar e executar

```powershell
dotnet build Rag.sln --no-restore
dotnet test Rag.sln --no-build --no-restore -m:1
dotnet run --project src/Rag.Api
```

Os testes de integração iniciam um PostgreSQL/pgvector isolado com Testcontainers; portanto, o engine Docker deve estar em execução. O `-m:1` serializa os projetos de teste e evita que hosts Windows com quantidade muito alta de processadores iniciem dezenas de nós MSBuild para uma suíte pequena.

Em outro processo, o Worker pode ser executado com:

```powershell
dotnet run --project src/Rag.Worker
```

Endpoints atuais da API:

- `GET /health/live`: verifica apenas que o processo responde e não acessa dependências externas;
- `GET /health/ready`: verifica a conexão real com PostgreSQL e responde `503` quando indisponível;
- `GET /health/dependencies`: expõe o estado da dependência PostgreSQL;
- `POST /v1/knowledge-bases`: cria uma base para o tenant autenticado; exige `rag.admin`;
- `GET /v1/knowledge-bases/{knowledgeBaseId}`: consulta uma base somente dentro do tenant autenticado; exige `rag.admin`;
- `POST /v1/knowledge-bases/{knowledgeBaseId}/documents`: recebe um `.txt` e cria versão, documento e job; exige `rag.ingest`;
- `GET /v1/ingestions/{jobId}`: consulta o status do job dentro do tenant; exige `rag.ingest`;
- `POST /v1/ingestions/{jobId}/retry`: recoloca jobs `Failed` ou `DeadLetter` na fila; exige `rag.ingest`;
- `GET /openapi/v1.json`: documento OpenAPI da fundação.

Respostas incluem `X-Request-ID`. Um identificador válido enviado nesse header é propagado; caso contrário, a API usa o identificador criado pelo host. Erros sem corpo são convertidos para RFC 9457 Problem Details com a extensão `requestId`.

Logs locais são estruturados em JSON e também percorrem o pipeline OpenTelemetry. O host não usa o Windows Event Log, permitindo execução com identidade sem privilégio administrativo.

## Configuração

Configurações usam `appsettings.json`, variáveis de ambiente com `__` ou outro provider padrão do .NET.

| Chave | Obrigatória | Default de Development | Finalidade |
| --- | --- | --- | --- |
| `Database__ConnectionString` | sim | PostgreSQL local do Compose | conexão Npgsql usada por API e Worker |
| `Database__CommandTimeoutSeconds` | sim | `30` | timeout de comandos, entre 1 e 300 segundos |
| `Authentication__ApiKey__HeaderName` | sim | `X-API-Key` | header da credencial máquina a máquina |
| `Authentication__ApiKey__Pepper` | quando há clientes | vazio | segredo externo usado no HMAC-SHA-256; mínimo de 32 caracteres |
| `Authentication__ApiKey__Clients` | não | vazio | clientes, tenants, hash, chatbot opcional e escopos |
| `Chunking__MaxTokens` | sim | `500` | máximo de tokens lexicais por chunk |
| `Chunking__OverlapTokens` | sim | `80` | tokens repetidos entre chunks consecutivos; deve ser menor que o máximo |
| `Embedding__Provider` | sim | `Deterministic` | fake determinístico disponível somente em Development/testes |
| `Embedding__Model` | sim | `deterministic-development-v1` | identificador fixado na versão e validado na resposta do provider |
| `Embedding__Dimensions` | sim | `1536` | dimensões fixas do esquema pgvector |
| `Embedding__BatchSize` | sim | `32` | quantidade máxima de chunks por chamada |
| `Embedding__MaxConcurrency` | sim | `2` | chamadas de embedding simultâneas por processo |
| `Embedding__RequestTimeoutSeconds` | sim | `30` | timeout de cada batch |
| `Storage__Provider` | sim | `Local` | somente `Local`, restrito a Development nesta fase |
| `Storage__LocalPath` | sim | `../../.data/documents` | raiz dedicada do storage local |
| `Uploads__MaxFileSizeBytes` | sim | `10485760` | tamanho máximo do conteúdo do arquivo |
| `Uploads__IdempotencyTtlHours` | sim | `24` | validade da resposta associada ao `Idempotency-Key` |
| `Jobs__LeaseDurationSeconds` | sim | `60` | duração do lease adquirido por Worker |
| `Jobs__MaxAttempts` | sim | `5` | tentativas antes de `DeadLetter` |
| `Jobs__BaseRetryDelaySeconds` | sim | `5` | base do backoff exponencial |
| `Jobs__MaxRetryDelaySeconds` | sim | `300` | teto do backoff com jitter |
| `Worker__MaxConcurrentJobs` | sim | `2` | consumidores concorrentes no processo Worker |
| `Worker__PollIntervalMilliseconds` | sim | `500` | espera quando nenhum job está disponível |
| `RateLimiting__TenantPermitLimit` | sim | `100` | requisições permitidas por tenant em cada janela |
| `RateLimiting__ChatbotPermitLimit` | sim | `50` | requisições permitidas por chatbot em cada janela |
| `RateLimiting__WindowSeconds` | sim | `60` | duração da janela fixa, entre 1 e 3.600 segundos |
| `OpenTelemetry__ServiceName` | sim | `Rag.Api` / `Rag.Worker` | nome do serviço em logs, métricas e traces |
| `OpenTelemetry__OtlpEndpoint` | não | ausente | endpoint OTLP absoluto; nenhum export ocorre quando ausente |

As opções são validadas no startup. Fora de Development, forneça a connection string por secret store ou variável de ambiente; não versione credenciais reais.

## Autenticação e autorização

A API aceita credenciais no formato `keyId.secret` pelo header `X-API-Key`. O segredo nunca é armazenado em claro: `SecretHash` é o Base64 de `HMAC-SHA-256(pepper, secret)`. O `pepper` e os hashes de clientes devem vir de user secrets, secret store ou variáveis de ambiente.

Para calcular um hash local sem registrar os valores:

```powershell
$pepper = Read-Host "Pepper"
$secret = Read-Host "API key secret"
$hmac = [System.Security.Cryptography.HMACSHA256]::new(
  [System.Text.Encoding]::UTF8.GetBytes($pepper))
$hash = [Convert]::ToBase64String(
  $hmac.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($secret)))
```

Configure um cliente de desenvolvimento com `dotnet user-secrets`; substitua todos os placeholders e não reutilize esses valores em produção:

```powershell
dotnet user-secrets set "Authentication:ApiKey:Pepper" "<pepper-com-32-ou-mais-caracteres>" --project src/Rag.Api
dotnet user-secrets set "Authentication:ApiKey:Clients:0:KeyId" "<identificador-publico>" --project src/Rag.Api
dotnet user-secrets set "Authentication:ApiKey:Clients:0:TenantId" "<tenant-uuid>" --project src/Rag.Api
dotnet user-secrets set "Authentication:ApiKey:Clients:0:SecretHash" "<hash-base64>" --project src/Rag.Api
dotnet user-secrets set "Authentication:ApiKey:Clients:0:Scopes:0" "rag.admin" --project src/Rag.Api
```

O tenant deve existir e estar ativo no banco. Quando `ChatbotId` é configurado para a credencial, o chatbot também deve pertencer ao mesmo tenant. O provisionamento inicial de tenants e credenciais é uma operação administrativa fora de banda nesta fase; nenhum endpoint retorna ou gera chaves.

Escopos disponíveis:

- `rag.admin`: administração de bases;
- `rag.ingest`: upload, status e retry de ingestões;
- `rag.retrieve`: reservado aos endpoints de consulta da Fase 7.

O tenant nunca é aceito no corpo da requisição. JSON com membros desconhecidos, inclusive `tenantId`, recebe `400` em Problem Details. Credencial ausente ou inválida recebe `401`; credencial válida sem o escopo exigido recebe `403`.

Os endpoints protegidos usam limites encadeados por tenant e, quando a credencial é vinculada a chatbot, também por chatbot. O excesso recebe `429` em Problem Details. Os contadores são locais a cada instância da API; em implantação com múltiplas réplicas, o limite agregado é aproximado e deve ser complementado no gateway/load balancer se houver necessidade de cota global.

## Upload e idempotência

O upload usa `multipart/form-data` e aceita exatamente uma seção de arquivo chamada `file`. O endpoint processa o corpo com streaming, sem model binding por `IFormFile`, e exige:

- header `Idempotency-Key` com até 200 caracteres ASCII visíveis;
- nome com extensão `.txt` e no máximo 512 caracteres;
- `Content-Type: text/plain`, com `charset=utf-8` quando informado;
- UTF-8 válido, pelo menos um caractere não branco e ausência de controles binários;
- conteúdo dentro de `Uploads__MaxFileSizeBytes`.

Exemplo:

```powershell
curl.exe `
  -H "X-API-Key: <keyId.secret>" `
  -H "Idempotency-Key: <identificador-unico>" `
  -F "file=@C:\caminho\manual.txt;type=text/plain;charset=utf-8" `
  http://localhost:5000/v1/knowledge-bases/<knowledge-base-id>/documents
```

Um arquivo válido é gravado com nome interno gerado pela aplicação e SHA-256 calculado durante a cópia. Em seguida, uma única transação cria `KnowledgeBaseVersion` em `Pending`, `Document` em `Uploaded`, `IngestionJob` em `Queued` e `IdempotencyRecord`. A resposta é `202 Accepted` com `documentId`, `versionId`, `jobId` e `statusUrl`.

Repetir a mesma requisição com a mesma chave devolve os mesmos IDs e `Idempotency-Replayed: true`. Reutilizar a chave com arquivo ou metadados diferentes devolve `409`. Falha de validação não cria versão, documento ou job; falha de banco remove o objeto preparado por compensação.

O provider local existe somente em Development e grava sob `.data/documents`, ignorado pelo Git. Ele não deve ser usado em produção; um adapter de object storage deverá substituir `IDocumentStorage`.

## Fila de ingestão

`IIngestionJobQueue` implementa aquisição, conclusão, falha e retry manual. A aquisição usa transação curta com `FOR UPDATE SKIP LOCKED`, token aleatório e `locked_until`. Assim, somente um Worker recebe cada lease.

Se o Worker desaparecer, outro pode readquirir o job após a expiração, com novo token e tentativa incrementada. Falhas transitórias entram em `Retrying` com backoff exponencial e jitter determinístico; a última tentativa vai para `DeadLetter`. Falhas permanentes vão para `Failed`. O endpoint de retry manual aceita somente `Failed` ou `DeadLetter` e reinicia o contador.

O Worker executa consumidores concorrentes limitados por configuração. Cada consumidor cria um escopo próprio, adquire e renova o lease, processa um documento e conclui o job na mesma transação que persiste o resultado final. Se o processo for encerrado durante o trabalho, nenhum resultado parcial é confirmado e outro consumidor pode reassumir o lease expirado.

## Chunking e embeddings

O texto armazenado é relido como UTF-8 estrito e normalizado de forma determinística: BOM inicial é removido, `CRLF`/`CR` viram `LF`, Unicode é convertido para NFC, espaços horizontais são compactados e sequências de linhas vazias são limitadas a uma separação de parágrafo.

`ITextChunker` usa tokens lexicais determinísticos, prefere terminar chunks em parágrafos e depois em sentenças, respeita `Chunking__MaxTokens` e aplica o overlap configurado. Cada chunk persiste:

- índice e offsets UTF-16 no texto normalizado;
- conteúdo e SHA-256;
- token count e hash da configuração de chunking;
- embedding `vector(1536)` e metadados JSON.

O provider recebe batches e todas as respostas são validadas contra modelo, dimensão, quantidade e tamanho dos vetores fixados na versão. Chamadas possuem timeout e um semáforo limita a concorrência. O provider `Deterministic` gera vetores normalizados e repetíveis para desenvolvimento e testes; ele não representa qualidade semântica e é bloqueado fora de Development.

Antes de chamar o provider, o processador procura chunks do mesmo tenant com conteúdo, configuração, modelo e dimensão compatíveis. Vetores encontrados são reutilizados; conteúdos repetidos dentro do próprio processamento também geram somente uma chamada. A gravação substitui atomicamente os chunks daquele documento, marca o documento `Indexed`, deixa a versão `Ready` e conclui o job. A versão nunca é marcada `Active` nesta fase.

Falhas antes do commit não persistem chunks. Falhas transitórias mantêm documento e versão em processamento para retry; falhas permanentes ou esgotamento marcam documento e versão como `Failed`. O retry manual restaura os estados de preparação antes de recolocar o job na fila.

## Persistência

As migrations criam a extensão `vector`, nove tabelas e os metadados de posição/configuração necessários ao processamento:

- `tenants`;
- `chatbots`;
- `knowledge_bases`;
- `knowledge_base_versions`;
- `documents`;
- `document_chunks`;
- `ingestion_jobs`;
- `query_logs`;
- `idempotency_records`.

O esquema usa:

- vetores `vector(1536)` e busca vetorial exata, sem HNSW nesta fase;
- coluna `search_vector` gerada com configuração full-text `simple` e índice GIN;
- chaves estrangeiras compostas para impedir relações cross-tenant;
- índice parcial único para permitir somente uma versão `Active` por base;
- timestamps UTC preenchidos pela persistência;
- `xmin` do PostgreSQL como token de concorrência otimista nas entidades mutáveis.

Toda consulta a chunks deve informar `tenant_id`, `knowledge_base_id` e `version_id`. O helper atual torna esses três filtros obrigatórios. A busca híbrida e os endpoints de negócio permanecem fora desta fase.

Para criar uma migração futura:

```powershell
dotnet ef migrations add NomeDaMigracao --project src/Rag.Infrastructure --startup-project src/Rag.Api --output-dir Persistence/Migrations
```

## Estrutura e dependências

```text
src/
  Rag.Api/             processo HTTP
  Rag.Application/     casos de uso e portas de providers
  Rag.Contracts/       contratos públicos compartilhados
  Rag.Domain/          regras de domínio
  Rag.Infrastructure/  adapters, persistência e observabilidade
  Rag.Worker/          processo assíncrono
tests/
  Rag.UnitTests/
  Rag.IntegrationTests/
  Rag.ArchitectureTests/
  Rag.LoadTests/       projeto reservado, ativado como suíte na Fase 11
docs/adr/
```

Domain e Contracts não dependem de outros projetos. Application depende somente deles; Infrastructure implementa as portas; API e Worker fazem a composição e não se referenciam. Testes de arquitetura inspecionam todos os `ProjectReference` de produção.

As portas atuais são `IEmbeddingProvider`, `ILanguageModelProvider`, `IDocumentStorage`, `IIngestionJobQueue` e `IClock`. Elas não selecionam fornecedor, endpoint ou credencial e todas as operações assíncronas aceitam cancelamento.

## Decisões e segurança

As decisões estão em `docs/adr/0001` a `0008`. Classificação de dados, fronteiras de confiança, ameaças e controles presentes ou planejados estão em `docs/threat-model.md`.

## Limites desta entrega

Não há provisionamento HTTP de tenants/chaves, scanner antimalware, adapter de object storage ou provider semântico de produção, ativação de versão, retrieval, chamada de LLM, `/metrics` Prometheus ou testes de carga. Versões processadas terminam em `Ready`; a troca blue/green para `Active` pertence exclusivamente à Fase 6.
