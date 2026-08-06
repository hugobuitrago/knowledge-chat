# ADR 0009: retrieval híbrido com Reciprocal Rank Fusion

- Status: aceito
- Data: 2026-08-06

## Contexto

Similaridade vetorial cobre relações semânticas, mas não é suficiente para códigos, siglas e termos exatos. Full-text search cobre esses termos, porém não substitui recuperação semântica. As duas escalas de score não são diretamente comparáveis, e uma ativação concorrente não pode fazer uma consulta combinar versões diferentes.

## Decisão

Cada consulta resolve a versão `Active` dentro do escopo de tenant, base e chatbot. O embedding da pergunta é validado contra modelo e dimensões da versão. Em seguida, uma transação de leitura `RepeatableRead` executa:

- top 20 por distância cosseno exata do pgvector (`<=>`);
- top 20 por `websearch_to_tsquery('simple', ...)` e `ts_rank_cd` sobre o `tsvector` armazenado;
- fusão em memória por Reciprocal Rank Fusion, com constante padrão 60;
- deduplicação por chunk, limite padrão de dois resultados por documento e top 8 final.

Todo SQL usa parâmetros posicionais e repete `tenant_id`, `knowledge_base_id`, `version_id` e o estado `Active`. A configuração full-text aceita somente `simple`, pois o `tsvector` persistido foi gerado com essa configuração.

Falha conhecida do provider de embeddings remove somente a estratégia vetorial. A busca lexical continua e a resposta retorna `degraded=true`. O reranker é uma porta explícita com implementação no-op no MVP; uma falha futura de reranking preserva o resultado do RRF e também marca degradação.

## Consequências

- Códigos e siglas continuam encontráveis mesmo quando o embedding falha.
- A consulta observa uma única versão ativa durante as duas estratégias.
- Busca vetorial permanece exata; HNSW continua condicionado a benchmark futuro.
- RRF usa posição, não tenta normalizar scores incompatíveis.
- O provider determinístico serve apenas a desenvolvimento e testes; qualidade semântica de produção depende de um adapter real.
