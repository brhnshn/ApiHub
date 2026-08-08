using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DockerPanel.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSmtpSelfSignedCert : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AcceptSelfSignedCert",
                table: "SmtpSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AcceptSelfSignedCert",
                table: "SmtpSettings");
        }
    }
}
