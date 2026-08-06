# ADR 0010: geração de resposta baseada em evidências

- Status: Aceita
- Data: 2026-08-06

## Contexto

O retrieval híbrido fornece chunks autorizados, mas um modelo de linguagem ainda pode usar conhecimento externo, obedecer a instruções presentes em documentos ou produzir citações inexistentes. Também precisamos preservar a disponibilidade do retrieval quando o provider de geração estiver indisponível.

## Decisão

`IQueryService` compõe retrieval e geração. O prompt de sistema declara os documentos como dados não confiáveis e proíbe seguir instruções encontradas neles. Pergunta e evidências são serializadas em JSON na última mensagem de usuário; documentos nunca ocupam a função `system`.

O limite de contexto usa estimativa conservadora de quatro caracteres por token e inclui custo fixo de metadados. Histórico, contexto, saída e duração da chamada possuem limites independentes. O provider recebe o token de cancelamento vinculado ao timeout configurado.

`ILanguageModelProvider` devolve a resposta e IDs estruturados de chunks. A aplicação aceita uma geração somente quando a resposta não está vazia e todos os IDs citados pertencem ao conjunto efetivamente incluído no prompt. Citação ausente ou desconhecida equivale a falha segura.

Sem evidência mínima, o modelo não é chamado. Em falha, o modo `EvidenceOnly` devolve mensagem fixa e os chunks enviados com `degraded=true`. O modo `SecondaryProvider` tenta um adapter registrado em `ISecondaryLanguageModelProvider` uma vez e, se ele também falhar ou não existir, usa o mesmo fallback somente com evidências. Retry, circuit breaker e bulkhead permanecem na Fase 9.

Streaming é uma capacidade opcional expressa por `IStreamingLanguageModelProvider`; o endpoint síncrono do MVP não depende dela.

## Consequências

- `/v1/query` nunca publica citações fora do contexto enviado ao modelo.
- indisponibilidade do LLM não afeta `/v1/retrieve`;
- o cliente consegue distinguir resposta completa, contexto insuficiente e estado degradado;
- o fake determinístico continua restrito a Development e deve ser substituído por adapter governado antes de produção;
- delimitação e validação reduzem o risco de prompt injection, mas não substituem avaliação contínua do modelo e políticas de saída.
