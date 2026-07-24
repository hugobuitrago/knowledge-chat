# ADR 0004: bases versionadas com ativação blue/green

- Status: aceito
- Data: 2026-07-22
- Atualização: 2026-07-23

## Contexto

Uma ingestão pode falhar parcialmente ou demorar. Consultas não podem observar mistura de chunks antigos e novos nem perder a versão previamente válida.

## Decisão

Cada alteração de corpus produzirá uma `KnowledgeBaseVersion` imutável. Documentos e chunks serão processados em uma versão não ativa. Somente uma versão pronta, com todos os documentos indexados, poderá ser ativada em transação curta. A versão anterior continuará consultável até o commit e depois será arquivada conforme política de retenção.

A seleção da versão ativa acontecerá no banco e fará parte dos filtros multi-tenant. Exclusão de documento criará nova versão ou invalidará uma versão em construção; nunca alterará silenciosamente a ativa.

Na Fase 2, um índice parcial único sobre `(tenant_id, knowledge_base_id)` quando o status é `Active` materializa a regra de uma única versão ativa. Chaves estrangeiras compostas repetem o `tenant_id` nas relações e impedem que documentos, chunks ou versões sejam associados a recursos de outro tenant.

## Consequências

- Consultas observam integralmente a versão anterior ou a nova.
- Reprocessamento exige espaço adicional temporário.
- Ativação concorrente é protegida pela restrição única no banco e por concorrência otimista via `xmin`; a orquestração transacional da ativação pertence à fase de ingestão.
- Chunks ativos são imutáveis.
