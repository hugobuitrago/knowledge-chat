using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rag.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class PreventActiveVersionChunkMutation : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE FUNCTION prevent_active_version_chunk_mutation()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $function$
            BEGIN
                IF TG_OP <> 'INSERT' AND EXISTS (
                    SELECT 1
                    FROM knowledge_base_versions AS version
                    WHERE version.tenant_id = OLD.tenant_id
                      AND version.knowledge_base_id = OLD.knowledge_base_id
                      AND version.id = OLD.version_id
                      AND version.status = 'Active'
                ) THEN
                    RAISE EXCEPTION 'Chunks of an active knowledge base version are immutable.'
                        USING ERRCODE = '55000';
                END IF;

                IF TG_OP <> 'DELETE' AND EXISTS (
                    SELECT 1
                    FROM knowledge_base_versions AS version
                    WHERE version.tenant_id = NEW.tenant_id
                      AND version.knowledge_base_id = NEW.knowledge_base_id
                      AND version.id = NEW.version_id
                      AND version.status = 'Active'
                ) THEN
                    RAISE EXCEPTION 'Chunks of an active knowledge base version are immutable.'
                        USING ERRCODE = '55000';
                END IF;

                IF TG_OP = 'DELETE' THEN
                    RETURN OLD;
                END IF;

                RETURN NEW;
            END;
            $function$;

            CREATE TRIGGER document_chunks_active_version_immutable
            BEFORE INSERT OR UPDATE OR DELETE ON document_chunks
            FOR EACH ROW EXECUTE FUNCTION prevent_active_version_chunk_mutation();
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DROP TRIGGER IF EXISTS document_chunks_active_version_immutable
                ON document_chunks;
            DROP FUNCTION IF EXISTS prevent_active_version_chunk_mutation();
            """);
    }
}
