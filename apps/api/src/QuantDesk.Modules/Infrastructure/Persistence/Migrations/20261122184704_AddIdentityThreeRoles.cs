using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuantDesk.Modules.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIdentityThreeRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "auth_version",
                table: "utilisateurs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "display_name",
                table: "utilisateurs",
                type: "character varying(150)",
                maxLength: 150,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "utilisateurs",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Active");

            migrationBuilder.CreateTable(
                name: "profils_joueurs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    utilisateur_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pseudo_public = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_profils_joueurs", x => x.id);
                    table.ForeignKey(
                        name: "FK_profils_joueurs_utilisateurs_utilisateur_id",
                        column: x => x.utilisateur_id,
                        principalTable: "utilisateurs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_utilisateurs_status",
                table: "utilisateurs",
                sql: "status IN ('Active', 'Suspended')");

            migrationBuilder.CreateIndex(
                name: "IX_profils_joueurs_utilisateur_id",
                table: "profils_joueurs",
                column: "utilisateur_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "profils_joueurs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_utilisateurs_status",
                table: "utilisateurs");

            migrationBuilder.DropColumn(
                name: "auth_version",
                table: "utilisateurs");

            migrationBuilder.DropColumn(
                name: "display_name",
                table: "utilisateurs");

            migrationBuilder.DropColumn(
                name: "status",
                table: "utilisateurs");
        }
    }
}
