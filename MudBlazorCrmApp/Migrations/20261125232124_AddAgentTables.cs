using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MudBlazorCrmApp.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS "AgentChat" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_AgentChat" PRIMARY KEY AUTOINCREMENT,
                    "UserId" TEXT NOT NULL,
                    "ChatKey" TEXT NOT NULL,
                    "Title" TEXT NOT NULL,
                    "HistoryJson" TEXT NOT NULL,
                    "LogJson" TEXT NOT NULL,
                    "CreatedDate" TEXT NOT NULL,
                    "UpdatedDate" TEXT NOT NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_AgentChat_UserId_ChatKey" ON "AgentChat" ("UserId", "ChatKey");
                CREATE TABLE IF NOT EXISTS "AgentTask" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_AgentTask" PRIMARY KEY AUTOINCREMENT,
                    "UserId" TEXT NOT NULL,
                    "ChatKey" TEXT NOT NULL,
                    "TaskSeq" INTEGER NOT NULL,
                    "Goal" TEXT NOT NULL,
                    "Summary" TEXT NOT NULL,
                    "TranscriptJson" TEXT NOT NULL,
                    "InputTokens" INTEGER NOT NULL,
                    "OutputTokens" INTEGER NOT NULL,
                    "CachedInputTokens" INTEGER NOT NULL,
                    "Cost" TEXT NOT NULL,
                    "ModelCalls" INTEGER NOT NULL,
                    "DurationMs" INTEGER NOT NULL,
                    "StartedDate" TEXT NOT NULL,
                    "CreatedDate" TEXT NOT NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_AgentTask_UserId_ChatKey_TaskSeq" ON "AgentTask" ("UserId", "ChatKey", "TaskSeq");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentChat");

            migrationBuilder.DropTable(
                name: "AgentTask");
        }
    }
}
