# PostgreSQL local

O ambiente local usa a imagem `pgvector/pgvector:0.8.5-pg18-bookworm` definida no `docker-compose.yml`. A extensão `vector` e todo o esquema são criados pela migration EF Core; não há script de inicialização paralelo às migrations.

A senha `rag_dev_only` é fixa, não secreta e exclusiva do ambiente local. Ela não deve ser reutilizada fora do Docker Compose de desenvolvimento.

