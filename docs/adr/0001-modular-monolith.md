# ADR 0001: monólito modular com API e Worker separados

- Status: aceito
- Data: 2026-07-22

## Contexto

O produto precisa manter o caminho de consulta disponível enquanto ingestões são executadas, mas ainda não justifica a complexidade operacional de microsserviços. A API e o processamento assíncrono têm perfis de escala e falha diferentes.

## Decisão

Adotaremos um monólito modular em uma única solução, com dois executáveis implantáveis separadamente:

- `Rag.Api` atende administração, retrieval, geração e endpoints operacionais;
- `Rag.Worker` executará ingestão e manutenção assíncrona;
- `Rag.Domain` não depende de outros projetos;
- `Rag.Contracts` não depende de outros projetos;
- `Rag.Application` depende apenas de Domain e Contracts;
- `Rag.Infrastructure` implementará portas de Application e pode depender de Domain e Contracts;
- API e Worker compõem Application, Contracts e Infrastructure, sem depender um do outro.

Embeddings, modelo de linguagem, armazenamento de documentos, fila de ingestão e relógio são portas explícitas em Application. Nenhum fornecedor ou credencial é selecionado nesta fase.

## Consequências

- API e Worker podem escalar e falhar de forma independente.
- O caminho de consulta não dependerá do processo Worker nem do object storage.
- Testes de arquitetura validam referências entre projetos.
- A solução permanece simples de desenvolver e implantar, ao custo de exigir disciplina nos limites modulares.

