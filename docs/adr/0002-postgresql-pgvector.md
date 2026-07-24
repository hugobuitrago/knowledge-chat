# ADR 0002: PostgreSQL com pgvector para busca vetorial

- Status: aceito
- Data: 2026-07-22
- Atualização: 2026-07-23

## Contexto

O MVP exige persistência relacional, isolamento multi-tenant, full-text search e similaridade vetorial. Introduzir um mecanismo de busca separado aumentaria custo e consistência operacional.

## Decisão

Usaremos PostgreSQL gerenciado com a extensão pgvector. Dados relacionais, vetores e índices lexicais permanecerão no mesmo banco. A busca vetorial começará exata; HNSW somente será criado depois de benchmark com corpus representativo.

Toda consulta a chunks deverá filtrar `tenant_id`, `knowledge_base_id` e `version_id`. EF Core será preferido para persistência comum e SQL parametrizado para a futura busca híbrida.

Na Fase 2, o esquema fixa embeddings em 1.536 dimensões, usa uma coluna `tsvector` gerada com a configuração `simple` e cria apenas índice GIN para busca lexical. A extensão, as tabelas e os índices pertencem às migrations da aplicação. O Compose local usa PostgreSQL 18 com pgvector 0.8.5; produção deverá usar uma oferta gerenciada compatível, configurada externamente.

## Consequências

- Uma única tecnologia atende transações, full-text e vetores no MVP.
- Backups e recuperação permanecem centralizados.
- O banco é uma dependência crítica e possui health check real nos endpoints de readiness e dependências.
- Escala e parâmetros de índice devem ser validados por medição, não presumidos.
