# ADR 0006: autenticação por API key e limites multi-tenant

- Status: aceito
- Data: 2026-07-23

## Contexto

O MVP precisa autenticar workloads máquina a máquina sem depender de um provedor OAuth2 já existente. A identidade autenticada deve determinar o tenant e os escopos, e nenhuma rota pode confiar em `tenantId` enviado pelo cliente.

## Decisão

A API usará um esquema configurável de API key no formato `keyId.secret`. O `keyId` é público e permite localizar a configuração; o segredo precisa ter pelo menos 32 caracteres e é verificado em tempo constante contra um HMAC-SHA-256 codificado em Base64. A chave do HMAC (`pepper`) é obrigatória quando houver clientes e deve vir de secret store ou variável de ambiente.

Cada cliente configurado referencia um tenant, um chatbot opcional e um ou mais dos escopos `rag.admin`, `rag.ingest` e `rag.retrieve`. A autenticação confirma que o tenant está ativo e, quando aplicável, que o chatbot pertence ao tenant. `ICurrentTenant` lê somente os claims produzidos pelo handler.

JSON usa rejeição de membros desconhecidos, impedindo a introdução de `tenantId` nos contratos. Consultas continuam repetindo o filtro de tenant no banco como defesa em profundidade.

O rate limiting usa janelas fixas encadeadas: uma partição por tenant e outra por chatbot quando a credencial é vinculada a um. Os contadores são mantidos em memória por instância da API, sem introduzir Redis no MVP.

## Consequências

- Chaves em claro não ficam no repositório, na configuração persistida nem nos logs.
- Credenciais podem ser recarregadas pela configuração, mas provisionamento, rotação e revogação continuam sendo operações fora de banda.
- Um tenant inativo ou um vínculo inválido de chatbot invalida a credencial.
- Os limites preservam justiça dentro de cada réplica, mas não formam uma cota global exata entre múltiplas réplicas.
- Um gateway ou load balancer deve complementar proteção contra abuso distribuído e DDoS.
- Ambientes com provedor de identidade corporativo poderão substituir o handler por JWT/OAuth2 mantendo claims, escopos e `ICurrentTenant`.
