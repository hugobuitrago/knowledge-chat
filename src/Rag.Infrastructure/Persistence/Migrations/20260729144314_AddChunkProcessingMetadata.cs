using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Rag.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddChunkProcessingMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_document_chunks_hash",
                table: "document_chunks");

            migrationBuilder.AddColumn<string>(
                name: "chunking_configuration_hash",
                table: "document_chunks",
                type: "character(64)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "end_offset",
                table: "document_chunks",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "start_offset",
                table: "document_chunks",
                type: "integer",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE document_chunks
                SET chunking_configuration_hash = repeat('0', 64),
                    start_offset = 0,
                    end_offset = char_length(content)
                """);

            migrationBuilder.AlterColumn<string>(
                name: "chunking_configuration_hash",
                table: "document_chunks",
                type: "character(64)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character(64)",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "end_offset",
                table: "document_chunks",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "start_offset",
                table: "document_chunks",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_document_chunks_embedding_reuse",
                table: "document_chunks",
                columns: new[] { "tenant_id", "content_hash", "chunking_configuration_hash" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_document_chunks_embedding_reuse",
                table: "document_chunks");

            migrationBuilder.DropColumn(
                name: "chunking_configuration_hash",
                table: "document_chunks");

            migrationBuilder.DropColumn(
                name: "end_offset",
                table: "document_chunks");

            migrationBuilder.DropColumn(
                name: "start_offset",
                table: "document_chunks");

            migrationBuilder.CreateIndex(
                name: "ux_document_chunks_hash",
                table: "document_chunks",
                columns: new[] { "version_id", "document_id", "content_hash" },
                unique: true);
        }
    }
}
