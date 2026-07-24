# ADR 0003: fila de jobs no PostgreSQL

- Status: aceito
- Data: 2026-07-22
- Atualização: 2026-07-23

## Contexto

A ingestão precisa ser assíncrona, recuperável e idempotente. Um broker dedicado não é necessário para o volume inicial e está fora do MVP.

## Decisão

Persistiremos jobs no PostgreSQL. Workers adquirirão leases curtos com `FOR UPDATE SKIP LOCKED`, `locked_until` e um token de lease. Estados, tentativas e próxima execução serão transacionais. Retentativas usarão backoff exponencial com jitter somente para falhas transitórias; esgotamento moverá o job para DeadLetter.

A porta `IIngestionJobQueue` pertence a Application. A Fase 2 criou o esquema persistente e seus índices. A Fase 4 implementa aquisição, conclusão, falha e retry manual.

Cada aquisição abre uma transação curta, marca leases finais expirados como `DeadLetter`, seleciona no máximo um job elegível com `FOR UPDATE SKIP LOCKED`, grava um token aleatório, incrementa a tentativa e confirma antes de devolver o lease. Apenas o token vigente pode completar ou liberar o job.

Falha transitória antes do limite usa backoff exponencial com jitter determinístico entre 80% e 120%, limitado pelo máximo configurado. Esgotamento vai para `DeadLetter`; falha permanente vai para `Failed`. Retry manual reinicia as tentativas somente nesses dois estados.

## Consequências

- Criação da versão, documento, job e registro idempotente compartilham uma transação.
- Não há broker adicional para operar no MVP.
- Polling e contenção precisam de métricas e limites.
- Interrupção de Worker não perde o job; o lease expirado é reassumido com novo token.
