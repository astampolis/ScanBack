using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ScanBack.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class IncidentLiveChat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IncidentChatMessages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    IncidentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SenderAccessId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SenderRole = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ClientMessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Body = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IncidentChatMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IncidentChatMessages_Incidents_IncidentId",
                        column: x => x.IncidentId,
                        principalTable: "Incidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IncidentChatMessages_IncidentId_Id",
                table: "IncidentChatMessages",
                columns: new[] { "IncidentId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_IncidentChatMessages_IncidentId_SenderAccessId_ClientMessageId",
                table: "IncidentChatMessages",
                columns: new[] { "IncidentId", "SenderAccessId", "ClientMessageId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IncidentChatMessages");
        }
    }
}
