# Modelo de ameaças e classificação de dados

## Escopo

Este documento cobre a API RAG, o Worker, PostgreSQL, object storage e provedores externos. Até a Fase 5, a fundação, persistência, autenticação máquina a máquina, upload, fila, chunking e embeddings estão implementados; controles dependentes de ativação e retrieval serão implementados nas fases correspondentes.

## Classificação

| Dado | Classificação padrão | Tratamento mínimo |
| --- | --- | --- |
| Chaves, tokens e connection strings | segredo | nunca versionar ou registrar; usar secret store/variáveis de ambiente |
| Documento original e chunks | confidencial do tenant | criptografia em trânsito e repouso, autorização e retenção definida |
| Prompt, pergunta, resposta e histórico | confidencial do tenant | minimizar coleta; não registrar conteúdo integral por padrão |
| Embeddings | confidencial derivado | mesmo isolamento e retenção do documento de origem |
| IDs de tenant, base, documento e job | interno | permitidos em telemetria estruturada sem conteúdo associado |
| Métricas agregadas e health status | operacional | sem payload, segredo ou identificador sensível |

O proprietário dos dados deve confirmar classificação, residência, retenção e requisitos regulatórios antes de produção.

## Fronteiras de confiança

1. Cliente para API: tráfego não confiável, sujeito a autenticação, autorização, limites e validação.
2. API/Worker para PostgreSQL e storage: identidade de workload com menor privilégio e TLS.
3. API/Worker para providers de embedding/LLM: possível saída de conteúdo confidencial, controlada por configuração e contrato do fornecedor.
4. API e Worker: processos separados; nenhum endpoint de consulta depende da disponibilidade do Worker.

## Ameaças e controles planejados

- **Acesso cross-tenant:** tenant vem exclusivamente da identidade autenticada; consultas de chunks exigem tenant, base e versão; chaves estrangeiras compostas rejeitam relações cross-tenant no banco.
- **Elevação de privilégio:** escopos distintos para administração, ingestão e retrieval; negação por padrão.
- **Injeção SQL:** comandos parametrizados e revisão específica do SQL de busca híbrida.
- **Prompt injection em documentos:** documentos serão tratados como dados; respostas usarão apenas evidências e citações enviadas ao modelo.
- **Upload malicioso ou exaustão:** somente `.txt`, streaming, limites de tamanho/quantidade/encoding e cálculo de hash.
- **Replay e duplicação:** `Idempotency-Key`, hashes e restrições únicas nas operações mutáveis.
- **Vazamento em logs/traces:** request ID e IDs técnicos são permitidos; documentos, prompts, credenciais e respostas integrais são proibidos por padrão.
- **Indisponibilidade de provider:** timeout, cancelamento, circuit breaker e fallbacks definidos nas fases de resiliência.
- **Versão parcial pesquisável:** ativação blue/green transacional e chunks imutáveis após prontidão.
- **Comprometimento de dependency/supply chain:** centralização de versões, restore determinístico, analyzers e revisão de vulnerabilidades no CI futuro.

## Controles presentes na fundação

- nullable e analyzers habilitados; warnings quebram o build;
- Problem Details não expõe stack trace e inclui request ID;
- request IDs recebidos são limitados a 128 caracteres ASCII visíveis;
- OpenTelemetry é configurável sem endpoint ou credencial real;
- liveness não acessa dependências; readiness e dependencies verificam PostgreSQL com comando mínimo e timeout;
- interfaces de chamadas externas exigem `CancellationToken`.
- relações persistentes usam chaves compostas com `tenant_id`, e testes negativos exercitam a rejeição cross-tenant;
- apenas uma versão `Active` por base é permitida por índice parcial único;
- timestamps são normalizados para UTC e entidades mutáveis usam `xmin` para concorrência otimista;
- credenciais reais não são versionadas; a credencial do Compose é explicitamente restrita ao desenvolvimento local.
- chaves de API são verificadas por HMAC-SHA-256 com pepper externo; segredo, header de autenticação e payload não são incluídos nos logs;
- escopos `rag.admin`, `rag.ingest` e `rag.retrieve` usam políticas de autorização distintas;
- tenant e chatbot são emitidos como claims somente depois da validação da chave e do vínculo no banco;
- payloads não podem informar `tenantId`, e membros JSON desconhecidos são rejeitados;
- endpoints protegidos possuem rate limiting encadeado por tenant e chatbot.
- upload usa streaming, aceita um único `.txt`, limita tamanho, valida MIME/UTF-8/conteúdo básico e calcula SHA-256 durante a gravação;
- nomes fornecidos pelo cliente nunca determinam o caminho físico; chaves de storage são geradas com IDs internos e protegidas contra path traversal;
- versão, documento, job e idempotência são criados atomicamente depois da validação completa;
- replay com a mesma chave retorna a resposta original e reutilização divergente recebe conflito;
- leases possuem token e expiração; `SKIP LOCKED` impede aquisição simultânea e jobs interrompidos podem ser recuperados.
- o Worker limita jobs e chamadas de embedding concorrentes, aplica timeout por batch e renova leases entre etapas;
- o texto é relido como UTF-8 estrito e normalizado antes do chunking determinístico;
- chunks persistem hashes de conteúdo e configuração, token count e offsets sem incluir conteúdo em logs;
- reutilização de embedding exige o mesmo tenant, conteúdo, configuração, modelo e dimensão, impedindo deduplicação observável entre tenants;
- chunks e estados finais são confirmados atomicamente após revalidar o lease; falha parcial não deixa uma versão `Active`;
- o fake determinístico é bloqueado fora de Development e não envia conteúdo para serviços externos.

## Pendências antes de produção

Scanner antimalware, object storage com criptografia/identidade de workload, provider semântico com governança de dados, políticas gerais de resiliência, retenção/exclusão, rotação e revogação automatizadas de chaves, auditoria e testes de segurança ofensivos pertencem às fases posteriores do plano. Storage e embeddings atuais são estritamente de desenvolvimento. O rate limiting atual é local a cada réplica e não substitui proteção DDoS no edge. A defesa em profundidade no banco não substitui a autorização da aplicação nem eventual Row-Level Security antes de produção.
