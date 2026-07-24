# ADR 0007: upload streaming, storage preparado e idempotência

- Status: aceito
- Data: 2026-07-23

## Contexto

Uploads podem ser grandes, malformados, repetidos ou interrompidos. Bufferizar arquivos por model binding aumenta pressão de memória e disco temporário. O arquivo também precisa existir antes do Worker processá-lo, mas PostgreSQL e object storage não compartilham uma transação distribuída.

## Decisão

O endpoint aceita `multipart/form-data` com exatamente uma seção `file` e usa `MultipartReader` diretamente sobre o corpo HTTP. Extensão, MIME, tamanho, UTF-8 e conteúdo básico são validados durante a leitura. A cópia calcula SHA-256 e grava em uma chave gerada pela aplicação, sem usar o nome do cliente como caminho.

O upload ocorre em duas etapas:

1. o arquivo validado é preparado no storage;
2. uma transação PostgreSQL cria versão pendente, documento, job e registro idempotente.

Se a transação falhar ou a requisição for replay/conflito, o objeto preparado é removido por compensação. Se a compensação falhar, o evento registra apenas o ID técnico, nunca o conteúdo ou nome do arquivo.

`Idempotency-Key` é único por tenant e operação. O request hash cobre base, nome normalizado, MIME e hash do conteúdo. A resposta `202` completa é armazenada como JSON. Mesma chave e mesmo hash repetem a resposta; mesma chave com hash diferente recebe `409`.

`IDocumentStorage` permanece uma porta de Application. A única implementação atual usa filesystem local e é rejeitada fora de Development.

## Consequências

- O arquivo não é materializado integralmente em memória pela API.
- Arquivos inválidos não produzem estado persistente nem versão pesquisável.
- A criação dos registros relacionais é atômica, mas consistência com storage usa compensação.
- Uma falha de processo entre gravar o objeto e iniciar a transação pode deixar objeto órfão; limpeza periódica por idade será necessária antes de produção.
- Produção exige adapter de object storage, criptografia, identidade de workload e scanner antimalware.
- O modelo e as dimensões de embedding são fixados na versão durante o upload, embora a geração de embeddings pertença à Fase 5.
