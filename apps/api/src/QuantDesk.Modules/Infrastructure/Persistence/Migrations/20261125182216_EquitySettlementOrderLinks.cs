using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace QuantDesk.Modules.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EquitySettlementOrderLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "buy_order_id",
                table: "equity_trade_settlements",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "sell_order_id",
                table: "equity_trade_settlements",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "session_id",
                table: "equity_trade_settlements",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_equity_trade_settlements_buy_order_id",
                table: "equity_trade_settlements",
                column: "buy_order_id");

            migrationBuilder.CreateIndex(
                name: "IX_equity_trade_settlements_sell_order_id",
                table: "equity_trade_settlements",
                column: "sell_order_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_equity_settlement_order_link",
                table: "equity_trade_settlements",
                sql: "(buy_order_id IS NULL AND sell_order_id IS NULL AND session_id IS NULL) OR (buy_order_id IS NOT NULL AND sell_order_id IS NOT NULL AND session_id IS NOT NULL AND buy_order_id <> sell_order_id)");

            migrationBuilder.AddForeignKey(
                name: "FK_equity_trade_settlements_equity_order_reservations_buy_orde~",
                table: "equity_trade_settlements",
                column: "buy_order_id",
                principalTable: "equity_order_reservations",
                principalColumn: "order_id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_equity_trade_settlements_equity_order_reservations_sell_ord~",
                table: "equity_trade_settlements",
                column: "sell_order_id",
                principalTable: "equity_order_reservations",
                principalColumn: "order_id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_equity_trade_settlements_equity_order_reservations_buy_orde~",
                table: "equity_trade_settlements");

            migrationBuilder.DropForeignKey(
                name: "FK_equity_trade_settlements_equity_order_reservations_sell_ord~",
                table: "equity_trade_settlements");

            migrationBuilder.DropIndex(
                name: "IX_equity_trade_settlements_buy_order_id",
                table: "equity_trade_settlements");

            migrationBuilder.DropIndex(
                name: "IX_equity_trade_settlements_sell_order_id",
                table: "equity_trade_settlements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_equity_settlement_order_link",
                table: "equity_trade_settlements");

            migrationBuilder.DropColumn(
                name: "buy_order_id",
                table: "equity_trade_settlements");

            migrationBuilder.DropColumn(
                name: "sell_order_id",
                table: "equity_trade_settlements");

            migrationBuilder.DropColumn(
                name: "session_id",
                table: "equity_trade_settlements");
        }
    }
}
