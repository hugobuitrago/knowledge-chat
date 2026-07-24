# ADR 0005: convenções de persistência com EF Core e PostgreSQL

- Status: aceito
- Data: 2026-07-23

## Contexto

O modelo multi-tenant precisa preservar isolamento, datas consistentes, concorrência segura e evolução reproduzível do esquema. Essas garantias não podem depender apenas de disciplina nos handlers futuros.

## Decisão

EF Core será usado na persistência comum, com Npgsql e configuração explícita de tabelas, colunas, chaves, índices e tipos PostgreSQL.

As seguintes convenções são obrigatórias:

- migrations da aplicação são a única fonte versionada do esquema e das extensões;
- todas as relações pertencentes a tenant usam chaves alternativas e estrangeiras compostas que incluem `tenant_id`;
- entidades mutáveis usam a coluna de sistema `xmin` como token de concorrência otimista;
- interceptors preenchem `CreatedAt` e `UpdatedAt` em UTC;
- vetores de chunks e a configuração da versão devem ter exatamente 1.536 dimensões;
- consultas de chunks exigem tenant, base e versão;
- SQL específico, quando necessário, deve ser parametrizado.

O banco local é descartável e existe somente para desenvolvimento. Ambientes compartilhados recebem connection strings por configuração externa.

## Consequências

- Parte importante do isolamento e das invariantes é verificada pelo próprio PostgreSQL.
- Atualizações concorrentes obsoletas falham em vez de sobrescrever dados silenciosamente.
- Alterar a dimensão de embeddings exige uma migration e decisão arquitetural explícita.
- Migrations precisam ser testadas contra PostgreSQL com pgvector real.
- A aplicação continua responsável por autenticação, autorização e escopo correto das consultas.
