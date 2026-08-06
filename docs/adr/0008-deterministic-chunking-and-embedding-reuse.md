# ADR 0008: chunking determinístico e reutilização de embeddings

- Status: aceito
- Data: 2026-07-29

## Contexto

A ingestão precisa produzir os mesmos chunks em retries, evitar gravações parciais e reduzir chamadas pagas quando um conteúdo já tiver embedding compatível. O esquema fixa modelo e dimensões na versão. A ativação blue/green pertence à fase seguinte e não pode ocorrer como efeito colateral do processamento.

## Decisão

O texto é decodificado como UTF-8 estrito e normalizado para Unicode NFC, newlines `LF`, espaços horizontais únicos e no máximo uma linha vazia entre parágrafos. O chunker usa tokens lexicais com offsets UTF-16, limite padrão de 500 tokens e overlap de 80. Dentro do limite, prefere a última quebra de parágrafo e depois a última quebra de sentença.

Cada chunk persiste conteúdo, SHA-256, token count, índice, offsets e o SHA-256 da versão do algoritmo e de seus parâmetros. Modelo e dimensões permanecem na `KnowledgeBaseVersion`, evitando duplicação inconsistente.

Embeddings são solicitados em batches. Um limite por processo controla chamadas concorrentes e cada batch possui timeout. Antes da chamada, embeddings são reutilizados somente quando tenant, conteúdo, hash de configuração, modelo e dimensões coincidem. A restrição única por posição garante idempotência de retry; o antigo índice único por hash foi substituído por um índice não único voltado à reutilização, pois conteúdo repetido em posições diferentes é válido.

Chunks, estados finais do documento/versão e conclusão do job são confirmados em uma transação curta que valida e bloqueia o lease vigente. A versão termina em `Ready`, nunca `Active`.

O provider `Deterministic` é um fake de desenvolvimento e testes. Ele gera vetores repetíveis e normalizados, não pretende representar semântica e é rejeitado fora de Development. Um provider real deverá implementar a porta `IEmbeddingProvider` sem alterar o processador.

## Consequências

- Retry após falha parcial não duplica chunks nem expõe uma versão parcial.
- Conteúdos compatíveis do mesmo tenant economizam chamadas de embedding sem criar um canal de deduplicação cross-tenant.
- Offsets descrevem o texto normalizado, não os bytes do arquivo original.
- O tokenizador lexical é estável e independente de fornecedor, mas sua contagem pode diferir da tokenização de um modelo externo; um adapter futuro deve manter o hash de configuração compatível apenas quando essa semântica for preservada.
- A Fase 6 passou a validar e ativar a versão no mesmo commit final, sem alterar as garantias de chunking e reutilização desta decisão.
