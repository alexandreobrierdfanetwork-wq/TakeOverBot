using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TakeOverBot.Migrations
{
    /// <inheritdoc />
    public partial class VoteParticipationRecap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VoteParticipations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PollMessageId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    ChannelId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    UserId = table.Column<ulong>(type: "INTEGER", nullable: false),
                    Voted = table.Column<bool>(type: "INTEGER", nullable: false),
                    ClosedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VoteParticipations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VoteRecapStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PeriodStartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RecapMessageIdsCsv = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VoteRecapStates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VoteParticipations_ClosedAt",
                table: "VoteParticipations",
                column: "ClosedAt");

            migrationBuilder.CreateIndex(
                name: "IX_VoteParticipations_PollMessageId_UserId",
                table: "VoteParticipations",
                columns: new[] { "PollMessageId", "UserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VoteParticipations");

            migrationBuilder.DropTable(
                name: "VoteRecapStates");
        }
    }
}
