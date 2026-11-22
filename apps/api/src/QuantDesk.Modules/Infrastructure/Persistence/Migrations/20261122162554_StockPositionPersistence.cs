using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuantDesk.Modules.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StockPositionPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "positions_action",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    compte_trading_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instrument_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    quantity = table.Column<long>(type: "bigint", nullable: false),
                    cost_basis = table.Column<decimal>(type: "numeric(28,8)", precision: 28, scale: 8, nullable: false),
                    realized_pnl = table.Column<decimal>(type: "numeric(28,8)", precision: 28, scale: 8, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_positions_action", x => x.id);
                    table.CheckConstraint("ck_positions_action_cost_basis_nonnegative", "cost_basis >= 0");
                    table.CheckConstraint("ck_positions_action_quantity_basis_consistent", "(quantity = 0 AND cost_basis = 0) OR (quantity > 0 AND cost_basis > 0)");
                    table.CheckConstraint("ck_positions_action_quantity_nonnegative", "quantity >= 0");
                    table.ForeignKey(
                        name: "FK_positions_action_portefeuilles_compte_trading_id",
                        column: x => x.compte_trading_id,
                        principalTable: "portefeuilles",
                        principalColumn: "compte_trading_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_positions_action_compte_trading_id_instrument_id",
                table: "positions_action",
                columns: new[] { "compte_trading_id", "instrument_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "positions_action");
        }
    }
}
