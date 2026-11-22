using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuantDesk.Modules.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TradingAccountFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "comptes_trading",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    cash_balance = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_comptes_trading", x => x.Id);
                    table.ForeignKey(
                        name: "FK_comptes_trading_utilisateurs_owner_user_id",
                        column: x => x.owner_user_id,
                        principalTable: "utilisateurs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "cash_journal",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    compte_trading_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    event_key = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cash_journal", x => x.Id);
                    table.ForeignKey(
                        name: "FK_cash_journal_comptes_trading_compte_trading_id",
                        column: x => x.compte_trading_id,
                        principalTable: "comptes_trading",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "portefeuilles",
                columns: table => new
                {
                    compte_trading_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_portefeuilles", x => x.compte_trading_id);
                    table.ForeignKey(
                        name: "FK_portefeuilles_comptes_trading_compte_trading_id",
                        column: x => x.compte_trading_id,
                        principalTable: "comptes_trading",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_cash_journal_compte_trading_id_event_key",
                table: "cash_journal",
                columns: new[] { "compte_trading_id", "event_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_comptes_trading_owner_user_id",
                table: "comptes_trading",
                column: "owner_user_id",
                unique: true,
                filter: "account_type = 'Personal'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cash_journal");

            migrationBuilder.DropTable(
                name: "portefeuilles");

            migrationBuilder.DropTable(
                name: "comptes_trading");
        }
    }
}
