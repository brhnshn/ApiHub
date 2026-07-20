using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DockerPanel.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CompleteDeploymentPipeline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ContainerPort",
                table: "Deployments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DomainName",
                table: "Deployments",
                type: "character varying(253)",
                maxLength: 253,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HostPort",
                table: "Deployments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProxyService",
                table: "Deployments",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestJson",
                table: "Deployments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RollbackClaim",
                table: "Deployments",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SslEnabled",
                table: "Deployments",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SubdomainName",
                table: "Deployments",
                type: "character varying(63)",
                maxLength: 63,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContainerPort",
                table: "Deployments");

            migrationBuilder.DropColumn(
                name: "DomainName",
                table: "Deployments");

            migrationBuilder.DropColumn(
                name: "HostPort",
                table: "Deployments");

            migrationBuilder.DropColumn(
                name: "ProxyService",
                table: "Deployments");

            migrationBuilder.DropColumn(
                name: "RequestJson",
                table: "Deployments");

            migrationBuilder.DropColumn(
                name: "RollbackClaim",
                table: "Deployments");

            migrationBuilder.DropColumn(
                name: "SslEnabled",
                table: "Deployments");

            migrationBuilder.DropColumn(
                name: "SubdomainName",
                table: "Deployments");
        }
    }
}
