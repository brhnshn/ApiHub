using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DockerPanel.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRevisionApiKeysAndDeployWizard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoRestart",
                table: "Projects",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "DatabaseName",
                table: "Projects",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DatabasePassword",
                table: "Projects",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DatabaseUser",
                table: "Projects",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "Projects",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EntryFile",
                table: "Projects",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EnvVariablesJson",
                table: "Projects",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Environment",
                table: "Projects",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FrameworkVersion",
                table: "Projects",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HealthCheckEndpoint",
                table: "Projects",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Logo",
                table: "Projects",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RunUser",
                table: "Projects",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RuntimeType",
                table: "Projects",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StartCommand",
                table: "Projects",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkingDirectory",
                table: "Projects",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "AverageResponseTimeMs",
                table: "ApiKeys",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<string>(
                name: "BaseUrl",
                table: "ApiKeys",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Category",
                table: "ApiKeys",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DailyLimit",
                table: "ApiKeys",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DefaultModel",
                table: "ApiKeys",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "ApiKeys",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EndDate",
                table: "ApiKeys",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FailedRequests",
                table: "ApiKeys",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "LastError",
                table: "ApiKeys",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastErrorDate",
                table: "ApiKeys",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastModifiedBy",
                table: "ApiKeys",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MonthlyLimit",
                table: "ApiKeys",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Provider",
                table: "ApiKeys",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RemainingQuota",
                table: "ApiKeys",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StartDate",
                table: "ApiKeys",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SuccessfulRequests",
                table: "ApiKeys",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "TotalRequests",
                table: "ApiKeys",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAt",
                table: "ApiKeys",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UsedQuota",
                table: "ApiKeys",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "ApiKeyProjectPermissions",
                columns: table => new
                {
                    ApiKeyId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiKeyProjectPermissions", x => new { x.ApiKeyId, x.ProjectId });
                    table.ForeignKey(
                        name: "FK_ApiKeyProjectPermissions_ApiKeys_ApiKeyId",
                        column: x => x.ApiKeyId,
                        principalTable: "ApiKeys",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ApiKeyProjectPermissions_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ApiKeyUsageLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ApiKeyId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestDate = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProjectName = table.Column<string>(type: "text", nullable: true),
                    Endpoint = table.Column<string>(type: "text", nullable: true),
                    ResponseTimeMs = table.Column<double>(type: "double precision", nullable: false),
                    HttpStatus = table.Column<int>(type: "integer", nullable: false),
                    TokenUsage = table.Column<int>(type: "integer", nullable: true),
                    Cost = table.Column<decimal>(type: "numeric", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiKeyUsageLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApiKeyUsageLogs_ApiKeys_ApiKeyId",
                        column: x => x.ApiKeyId,
                        principalTable: "ApiKeys",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeyProjectPermissions_ProjectId",
                table: "ApiKeyProjectPermissions",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeyUsageLogs_ApiKeyId",
                table: "ApiKeyUsageLogs",
                column: "ApiKeyId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApiKeyProjectPermissions");

            migrationBuilder.DropTable(
                name: "ApiKeyUsageLogs");

            migrationBuilder.DropColumn(
                name: "AutoRestart",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "DatabaseName",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "DatabasePassword",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "DatabaseUser",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "EntryFile",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "EnvVariablesJson",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "Environment",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "FrameworkVersion",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "HealthCheckEndpoint",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "Logo",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "RunUser",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "RuntimeType",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "StartCommand",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "WorkingDirectory",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "AverageResponseTimeMs",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "BaseUrl",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "DailyLimit",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "DefaultModel",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "EndDate",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "FailedRequests",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "LastError",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "LastErrorDate",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "LastModifiedBy",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "MonthlyLimit",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "Provider",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "RemainingQuota",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "StartDate",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "SuccessfulRequests",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "TotalRequests",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "UsedQuota",
                table: "ApiKeys");
        }
    }
}
