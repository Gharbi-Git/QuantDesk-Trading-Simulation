using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuantDesk.Modules.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EquityOrderReservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "equity_order_reservations",
                columns: table => new
                {
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instrument_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    side = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    original_quantity = table.Column<long>(type: "bigint", nullable: false),
                    filled_quantity = table.Column<long>(type: "bigint", nullable: false),
                    limit_price = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_equity_order_reservations", x => x.order_id);
                    table.CheckConstraint("ck_equity_reservation_price", "limit_price > 0 AND original_quantity * limit_price <= 9999999999999999.9999");
                    table.CheckConstraint("ck_equity_reservation_quantity", "original_quantity > 0 AND filled_quantity >= 0 AND filled_quantity <= original_quantity");
                    table.CheckConstraint("ck_equity_reservation_side", "side IN ('Buy', 'Sell')");
                    table.CheckConstraint("ck_equity_reservation_state", "(status = 'Filled' AND filled_quantity = original_quantity) OR (status IN ('Active', 'Released') AND filled_quantity < original_quantity)");
                    table.CheckConstraint("ck_equity_reservation_status", "status IN ('Active', 'Filled', 'Released')");
                    table.CheckConstraint("ck_equity_reservation_version", "version >= 0");
                    table.ForeignKey(
                        name: "FK_equity_order_reservations_comptes_trading_account_id",
                        column: x => x.account_id,
                        principalTable: "comptes_trading",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_equity_order_reservations_account_id_status_side_instrument~",
                table: "equity_order_reservations",
                columns: new[] { "account_id", "status", "side", "instrument_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "equity_order_reservations");
        }
    }
}
