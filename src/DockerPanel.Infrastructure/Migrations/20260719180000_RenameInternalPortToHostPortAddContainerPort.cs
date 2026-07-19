using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using DockerPanel.Infrastructure.Data;

#nullable disable

namespace DockerPanel.Infrastructure.Migrations
{
    [DbContext(typeof(DockerPanelDbContext))]
    [Migration("20260719180000_RenameInternalPortToHostPortAddContainerPort")]
    public partial class RenameInternalPortToHostPortAddContainerPort : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. InternalPort kolonunu HostPort olarak yeniden adlandır
            migrationBuilder.RenameColumn(
                name: "InternalPort",
                table: "Projects",
                newName: "HostPort");

            // 2. ContainerPort kolonunu nullable olarak ekle (mevcut kayıtlar NULL kalır)
            migrationBuilder.AddColumn<int>(
                name: "ContainerPort",
                table: "Projects",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // ContainerPort kolonunu kaldır
            migrationBuilder.DropColumn(
                name: "ContainerPort",
                table: "Projects");

            // HostPort → InternalPort geri al
            migrationBuilder.RenameColumn(
                name: "HostPort",
                table: "Projects",
                newName: "InternalPort");
        }
    }
}
