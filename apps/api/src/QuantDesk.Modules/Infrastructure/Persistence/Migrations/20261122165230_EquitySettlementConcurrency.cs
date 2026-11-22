using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuantDesk.Modules.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EquitySettlementConcurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "version",
                table: "positions_action",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "version",
                table: "comptes_trading",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "equity_trade_settlements",
                columns: table => new
                {
                    execution_id = table.Column<Guid>(type: "uuid", nullable: false),
                    buyer_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seller_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    instrument_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    quantity = table.Column<long>(type: "bigint", nullable: false),
                    execution_price = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false),
                    settled_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_equity_trade_settlements", x => x.execution_id);
                    table.CheckConstraint("ck_equity_settlement_different_accounts", "buyer_account_id <> seller_account_id");
                    table.CheckConstraint("ck_equity_settlement_price_positive", "execution_price > 0");
                    table.CheckConstraint("ck_equity_settlement_quantity_positive", "quantity > 0");
                    table.ForeignKey(
                        name: "FK_equity_trade_settlements_comptes_trading_buyer_account_id",
                        column: x => x.buyer_account_id,
                        principalTable: "comptes_trading",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_equity_trade_settlements_comptes_trading_seller_account_id",
                        column: x => x.seller_account_id,
                        principalTable: "comptes_trading",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_comptes_trading_cash_nonnegative",
                table: "comptes_trading",
                sql: "cash_balance >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_equity_trade_settlements_buyer_account_id",
                table: "equity_trade_settlements",
                column: "buyer_account_id");

            migrationBuilder.CreateIndex(
                name: "IX_equity_trade_settlements_seller_account_id",
                table: "equity_trade_settlements",
                column: "seller_account_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "equity_trade_settlements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_comptes_trading_cash_nonnegative",
                table: "comptes_trading");

            migrationBuilder.DropColumn(
                name: "version",
                table: "positions_action");

            migrationBuilder.DropColumn(
                name: "version",
                table: "comptes_trading");
        }
    }
}
