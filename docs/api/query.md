# `POST /v1/query`

Gera uma resposta baseada exclusivamente nos chunks recuperados da versão ativa da base. Exige API key com o escopo `rag.retrieve`; tenant e chatbot são derivados da credencial e nunca podem ser enviados no payload.

## Requisição

```json
{
  "knowledgeBaseId": "11111111-1111-1111-1111-111111111111",
  "query": "Qual é o código de suporte?",
  "history": [
    {
      "role": "user",
      "content": "Estou consultando o manual atual."
    }
  ]
}
```

`history` é opcional e aceita somente os papéis `user` e `assistant`. Quantidade de mensagens e caracteres são limitados por `Generation`. A pergunta usa o limite de `Retrieval__MaxQueryLength`.

## Resposta completa

```json
{
  "knowledgeBaseId": "11111111-1111-1111-1111-111111111111",
  "versionId": "22222222-2222-2222-2222-222222222222",
  "answer": "O código documentado é SAFE-42.",
  "model": "provider-model",
  "degraded": false,
  "insufficientContext": false,
  "citations": [
    {
      "chunkId": "33333333-3333-3333-3333-333333333333",
      "source": {
        "documentId": "44444444-4444-4444-4444-444444444444",
        "fileName": "manual.txt",
        "chunkIndex": 0,
        "startOffset": 0,
        "endOffset": 31
      }
    }
  ],
  "evidence": [
    {
      "chunkId": "33333333-3333-3333-3333-333333333333",
      "content": "O código documentado é SAFE-42.",
      "score": 0.03278688524590164,
      "source": {
        "documentId": "44444444-4444-4444-4444-444444444444",
        "fileName": "manual.txt",
        "chunkIndex": 0,
        "startOffset": 0,
        "endOffset": 31
      }
    }
  ]
}
```

`citations` é sempre um subconjunto de `evidence`, e `evidence` contém somente o conteúdo efetivamente enviado ao modelo depois da aplicação do orçamento de contexto.

## Estados seguros

- `insufficientContext=true`: não havia evidência suficiente; o LLM não foi chamado e a resposta é fixa.
- `degraded=true`: retrieval parcial, provider secundário utilizado ou geração indisponível. Quando nenhum provider produz saída válida, `model` e `citations` são nulos/vazios e as evidências continuam disponíveis.
- `404`: não existe versão ativa autorizada para a combinação tenant/base/chatbot.
- `400`: UUID, pergunta ou histórico inválido.

O endpoint normal é não streaming. Adapters podem implementar `IStreamingLanguageModelProvider`, mas nenhum cliente depende dessa capacidade no MVP.
