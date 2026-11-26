using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MudBlazorCrmApp.Migrations
{
    /// <inheritdoc />
    public partial class AgentSchemaIndexesAndPrecision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_AgentChat_UserId_ChatKey" ON "AgentChat" ("UserId", "ChatKey");
                CREATE INDEX IF NOT EXISTS "IX_AgentChat_UserId_UpdatedDate" ON "AgentChat" ("UserId", "UpdatedDate");
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_AgentTask_UserId_ChatKey_TaskSeq" ON "AgentTask" ("UserId", "ChatKey", "TaskSeq");
                CREATE INDEX IF NOT EXISTS "IX_AgentTask_UserId_CreatedDate" ON "AgentTask" ("UserId", "CreatedDate");
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_AgentKnowledge_FactHash" ON "AgentKnowledge" ("FactHash");
                CREATE INDEX IF NOT EXISTS "IX_AgentKnowledge_IsRetired" ON "AgentKnowledge" ("IsRetired");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS "IX_AgentChat_UserId_UpdatedDate";
                DROP INDEX IF EXISTS "IX_AgentTask_UserId_CreatedDate";
                DROP INDEX IF EXISTS "IX_AgentKnowledge_IsRetired";
                """);
        }
    }
}
