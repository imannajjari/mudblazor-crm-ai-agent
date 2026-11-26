using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MudBlazorCrmApp.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentKnowledge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS "AgentKnowledge" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_AgentKnowledge" PRIMARY KEY AUTOINCREMENT,
                    "FactHash" TEXT NOT NULL,
                    "Topic" TEXT NOT NULL,
                    "Fact" TEXT NOT NULL,
                    "LanguageId" TEXT NULL,
                    "CreatedBy" TEXT NOT NULL,
                    "CreatedDate" TEXT NOT NULL,
                    "LastUsedDate" TEXT NULL,
                    "UseCount" INTEGER NOT NULL DEFAULT 0,
                    "ConfirmCount" INTEGER NOT NULL DEFAULT 1,
                    "NotHelpedCount" INTEGER NOT NULL DEFAULT 0,
                    "IsRetired" INTEGER NOT NULL DEFAULT 0,
                    "ModifiedBy" TEXT NULL,
                    "ModifiedDate" TEXT NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_AgentKnowledge_FactHash" ON "AgentKnowledge" ("FactHash");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP TABLE IF EXISTS "AgentKnowledge";""");
        }
    }
}
