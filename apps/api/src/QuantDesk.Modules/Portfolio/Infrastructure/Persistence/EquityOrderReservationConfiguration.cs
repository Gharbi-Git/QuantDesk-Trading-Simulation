using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using QuantDesk.Modules.Portfolio.Domain.Entities;

namespace QuantDesk.Modules.Portfolio.Infrastructure.Persistence;

public sealed class EquityOrderReservationConfiguration : IEntityTypeConfiguration<EquityOrderReservation>
{
    public void Configure(EntityTypeBuilder<EquityOrderReservation> entity)
    {
        entity.ToTable("equity_order_reservations", table =>
        {
            table.HasCheckConstraint("ck_equity_reservation_side", "side IN ('Buy', 'Sell')");
            table.HasCheckConstraint("ck_equity_reservation_status", "status IN ('Active', 'Filled', 'Released')");
            table.HasCheckConstraint("ck_equity_reservation_quantity", "original_quantity > 0 AND filled_quantity >= 0 AND filled_quantity <= original_quantity");
            table.HasCheckConstraint("ck_equity_reservation_state", "(status = 'Filled' AND filled_quantity = original_quantity) OR (status IN ('Active', 'Released') AND filled_quantity < original_quantity)");
            table.HasCheckConstraint("ck_equity_reservation_price", "limit_price > 0 AND original_quantity * limit_price <= 9999999999999999.9999");
            table.HasCheckConstraint("ck_equity_reservation_version", "version >= 0");
        });
        entity.HasKey(r => r.OrderId);
        entity.Property(r => r.OrderId).HasColumnName("order_id").ValueGeneratedNever();
        entity.Property(r => r.AccountId).HasColumnName("account_id");
        entity.Property(r => r.SessionId).HasColumnName("session_id");
        entity.Property(r => r.InstrumentId).HasColumnName("instrument_id").HasMaxLength(128).IsRequired();
        entity.Property(r => r.Side).HasColumnName("side").HasConversion<string>().HasMaxLength(8).IsRequired();
        entity.Property(r => r.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16).IsRequired();
        entity.Property(r => r.OriginalQuantity).HasColumnName("original_quantity");
        entity.Property(r => r.FilledQuantity).HasColumnName("filled_quantity");
        entity.Property(r => r.LimitPrice).HasColumnName("limit_price").HasPrecision(20, 4);
        entity.Property(r => r.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone");
        entity.Property(r => r.Version).HasColumnName("version").IsConcurrencyToken();
        entity.Ignore(r => r.RemainingQuantity);
        entity.Ignore(r => r.ReservedCash);
        entity.Ignore(r => r.ReservedShares);
        entity.HasIndex(r => new { r.AccountId, r.Status, r.Side, r.InstrumentId });
        entity.HasOne<CompteTrading>().WithMany().HasForeignKey(r => r.AccountId).OnDelete(DeleteBehavior.Restrict);
    }
}
