# POST /v1/retrieve

Recupera evidências da versão ativa de uma base. Requer credencial com escopo `rag.retrieve`. O tenant e o chatbot opcional vêm exclusivamente da identidade autenticada.

## Request

```http
POST /v1/retrieve
Content-Type: application/json
X-API-Key: <keyId.secret>

{
  "knowledgeBaseId": "11111111-1111-1111-1111-111111111111",
  "query": "ZX-81"
}
```

`query` é obrigatória e possui limite configurável, com default de 2.000 caracteres. Membros desconhecidos, incluindo `tenantId`, recebem `400`.

## Response

```json
{
  "knowledgeBaseId": "11111111-1111-1111-1111-111111111111",
  "versionId": "22222222-2222-2222-2222-222222222222",
  "degraded": false,
  "results": [
    {
      "chunkId": "33333333-3333-3333-3333-333333333333",
      "content": "O identificador ZX-81 aparece no catálogo.",
      "score": 0.0325,
      "source": {
        "documentId": "44444444-4444-4444-4444-444444444444",
        "fileName": "catalogo.txt",
        "chunkIndex": 0,
        "startOffset": 0,
        "endOffset": 42
      }
    }
  ]
}
```

`degraded=true` indica que o embedding da pergunta ou o reranker falhou e o melhor resultado disponível foi devolvido. Ausência de uma versão ativa dentro do escopo autorizado recebe `404`.
