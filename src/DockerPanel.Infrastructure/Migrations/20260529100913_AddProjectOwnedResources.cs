using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DockerPanel.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectOwnedResources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ProjectId",
                table: "DnsRecords",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ProjectId",
                table: "DatabaseSchemas",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DnsRecords_ProjectId",
                table: "DnsRecords",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseSchemas_ProjectId",
                table: "DatabaseSchemas",
                column: "ProjectId");

            migrationBuilder.AddForeignKey(
                name: "FK_DatabaseSchemas_Projects_ProjectId",
                table: "DatabaseSchemas",
                column: "ProjectId",
                principalTable: "Projects",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_DnsRecords_Projects_ProjectId",
                table: "DnsRecords",
                column: "ProjectId",
                principalTable: "Projects",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_DatabaseSchemas_Projects_ProjectId",
                table: "DatabaseSchemas");

            migrationBuilder.DropForeignKey(
                name: "FK_DnsRecords_Projects_ProjectId",
                table: "DnsRecords");

            migrationBuilder.DropIndex(
                name: "IX_DnsRecords_ProjectId",
                table: "DnsRecords");

            migrationBuilder.DropIndex(
                name: "IX_DatabaseSchemas_ProjectId",
                table: "DatabaseSchemas");

            migrationBuilder.DropColumn(
                name: "ProjectId",
                table: "DnsRecords");

            migrationBuilder.DropColumn(
                name: "ProjectId",
                table: "DatabaseSchemas");
        }
    }
}
