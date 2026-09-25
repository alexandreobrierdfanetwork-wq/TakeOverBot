using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TakeOverBot.Migrations
{
    /// <inheritdoc />
    public partial class VotePollThresholdReminders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CreatedAt",
                table: "VotePolls",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.Sql("""
                UPDATE VotePolls
                SET CreatedAt = CASE
                    WHEN RemindAt > 0 AND ExpiresAt > RemindAt
                        THEN RemindAt - (ExpiresAt - RemindAt)
                    ELSE ExpiresAt - 86400
                END
                WHERE CreatedAt = 0;
                """);

            migrationBuilder.AddColumn<bool>(
                name: "Remind72Sent",
                table: "VotePolls",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "Remind24Sent",
                table: "VotePolls",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "Remind3Sent",
                table: "VotePolls",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_VotePolls_MessageId",
                table: "VotePolls",
                column: "MessageId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_VotePolls_MessageId",
                table: "VotePolls");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "VotePolls");

            migrationBuilder.DropColumn(
                name: "Remind72Sent",
                table: "VotePolls");

            migrationBuilder.DropColumn(
                name: "Remind24Sent",
                table: "VotePolls");

            migrationBuilder.DropColumn(
                name: "Remind3Sent",
                table: "VotePolls");
        }
    }
}
