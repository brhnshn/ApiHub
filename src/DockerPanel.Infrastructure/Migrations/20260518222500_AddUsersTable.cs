using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DockerPanel.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUsersTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Username = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    PasswordHash = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DatabaseSchemas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DbName = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    DbUser = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DatabaseSchemas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DatabaseSchemas_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DnsRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Name = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false),
                    Ttl = table.Column<int>(type: "integer", nullable: false, defaultValue: 3600),
                    Proxied = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    CloudflareRecordId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DnsRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DnsRecords_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MailAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmailAddress = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    QuotaBytes = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailAccounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MailAccounts_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Projects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DockerContainerId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ImageOrPath = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    MemoryLimitBytes = table.Column<long>(type: "bigint", nullable: false),
                    CpuCount = table.Column<double>(type: "double precision", nullable: false),
                    InternalPort = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Projects_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Subdomains",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubdomainName = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    DomainName = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    SslEnabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Subdomains", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Subdomains_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Subdomains_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseSchemas_DbName",
                table: "DatabaseSchemas",
                column: "DbName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseSchemas_DbUser",
                table: "DatabaseSchemas",
                column: "DbUser",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DatabaseSchemas_UserId",
                table: "DatabaseSchemas",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_DnsRecords_UserId",
                table: "DnsRecords",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_MailAccounts_EmailAddress",
                table: "MailAccounts",
                column: "EmailAddress",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MailAccounts_UserId",
                table: "MailAccounts",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Projects_Name",
                table: "Projects",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Projects_UserId",
                table: "Projects",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Subdomains_ProjectId",
                table: "Subdomains",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_Subdomains_SubdomainName_DomainName",
                table: "Subdomains",
                columns: new[] { "SubdomainName", "DomainName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Subdomains_UserId",
                table: "Subdomains",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Username",
                table: "Users",
                column: "Username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DatabaseSchemas");

            migrationBuilder.DropTable(
                name: "DnsRecords");

            migrationBuilder.DropTable(
                name: "MailAccounts");

            migrationBuilder.DropTable(
                name: "Subdomains");

            migrationBuilder.DropTable(
                name: "Projects");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}
