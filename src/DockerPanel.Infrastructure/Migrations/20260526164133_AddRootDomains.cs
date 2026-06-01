using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DockerPanel.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRootDomains : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RootDomains",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    CloudflareToken = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CloudflareZoneId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RootDomains", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RootDomains_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RootDomains_Name",
                table: "RootDomains",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RootDomains_UserId",
                table: "RootDomains",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RootDomains");
        }
    }
}
