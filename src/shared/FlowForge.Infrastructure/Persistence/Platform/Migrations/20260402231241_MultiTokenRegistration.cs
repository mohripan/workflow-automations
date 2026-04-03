using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FlowForge.Infrastructure.Persistence.Platform.Migrations
{
    /// <inheritdoc />
    public partial class MultiTokenRegistration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RegistrationTokenHash",
                table: "HostGroups");

            migrationBuilder.CreateTable(
                name: "RegistrationTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HostGroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Label = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegistrationTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RegistrationTokens_HostGroups_HostGroupId",
                        column: x => x.HostGroupId,
                        principalTable: "HostGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RegistrationTokens_HostGroupId",
                table: "RegistrationTokens",
                column: "HostGroupId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RegistrationTokens");

            migrationBuilder.AddColumn<string>(
                name: "RegistrationTokenHash",
                table: "HostGroups",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }
    }
}
