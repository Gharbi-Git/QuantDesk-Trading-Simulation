using Microsoft.EntityFrameworkCore;
using QuantDesk.Modules.Administration.Domain.Entities;
using QuantDesk.Modules.Portfolio.Domain.Entities;

namespace QuantDesk.Modules.Infrastructure.Persistence;

public sealed class QuantDeskDbContext : DbContext
{
    public QuantDeskDbContext(DbContextOptions<QuantDeskDbContext> options)
        : base(options)
    {
    }

    public DbSet<Utilisateur> Utilisateurs => Set<Utilisateur>();
    public DbSet<CompteTrading> ComptesTrading => Set<CompteTrading>();
    public DbSet<Portefeuille> Portefeuilles => Set<Portefeuille>();
    public DbSet<CashJournalEntry> CashJournal => Set<CashJournalEntry>();
    public DbSet<PositionAction> PositionsAction => Set<PositionAction>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Utilisateur>(entity =>
        {
            entity.ToTable("utilisateurs");
            entity.HasKey(user => user.Id);
            entity.Property(user => user.Id).HasColumnName("id");
            entity.Property(user => user.KeycloakSubject)
                .HasColumnName("keycloak_subject")
                .HasMaxLength(255)
                .IsRequired();
            entity.HasIndex(user => user.KeycloakSubject).IsUnique();
            entity.Property(user => user.Username)
                .HasColumnName("username")
                .HasMaxLength(150)
                .IsRequired();
            entity.Property(user => user.Email)
                .HasColumnName("email")
                .HasMaxLength(320);
            entity.Property(user => user.CreatedAtUtc)
                .HasColumnName("created_at_utc")
                .HasColumnType("timestamp with time zone")
                .IsRequired();
        });

        modelBuilder.Entity<CompteTrading>(entity =>
        {
            entity.ToTable("comptes_trading");
            entity.HasKey(a => a.Id);
            entity.Property(a => a.OwnerUserId)
                .HasColumnName("owner_user_id")
                .IsRequired();
            entity.Property(a => a.AccountType)
                .HasColumnName("account_type")
                .HasMaxLength(30)
                .IsRequired();
            entity.Property(a => a.Currency)
                .HasColumnName("currency")
                .HasMaxLength(3)
                .IsRequired();
            entity.Property(a => a.CashBalance)
                .HasColumnName("cash_balance")
                .HasPrecision(20, 4);
            entity.Property(a => a.CreatedAtUtc)
                .HasColumnName("created_at_utc");
            entity.HasIndex(a => a.OwnerUserId)
                .IsUnique()
                .HasFilter("account_type = 'Personal'");
            entity.HasOne<Utilisateur>()
                .WithMany()
                .HasForeignKey(a => a.OwnerUserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(a => a.Portefeuille)
                .WithOne(p => p.CompteTrading)
                .HasForeignKey<Portefeuille>(p => p.CompteTradingId);
            entity.HasMany(a => a.Journal)
                .WithOne(j => j.CompteTrading)
                .HasForeignKey(j => j.CompteTradingId);
        });

        modelBuilder.Entity<Portefeuille>(entity =>
        {
            entity.ToTable("portefeuilles");
            entity.HasKey(p => p.CompteTradingId);
            entity.Property(p => p.CompteTradingId)
                .HasColumnName("compte_trading_id");
            entity.HasMany(p => p.Positions)
                .WithOne(p => p.Portefeuille)
                .HasForeignKey(p => p.CompteTradingId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CashJournalEntry>(entity =>
        {
            entity.ToTable("cash_journal");
            entity.HasKey(j => j.Id);
            entity.Property(j => j.CompteTradingId)
                .HasColumnName("compte_trading_id");
            entity.Property(j => j.Amount)
                .HasColumnName("amount")
                .HasPrecision(20, 4);
            entity.Property(j => j.EventKey)
                .HasColumnName("event_key")
                .HasMaxLength(150)
                .IsRequired();
            entity.Property(j => j.CreatedAtUtc)
                .HasColumnName("created_at_utc");
            entity.HasIndex(j => new { j.CompteTradingId, j.EventKey })
                .IsUnique();
        });

        // QD-050/QD-056: a single long-only stock holding per instrument
        // in each portfolio. The account ID is also the portfolio PK.
        modelBuilder.Entity<PositionAction>(entity =>
        {
            entity.ToTable("positions_action", table =>
            {
                table.HasCheckConstraint(
                    "ck_positions_action_quantity_nonnegative", "quantity >= 0");
                table.HasCheckConstraint(
                    "ck_positions_action_cost_basis_nonnegative", "cost_basis >= 0");
                table.HasCheckConstraint(
                    "ck_positions_action_quantity_basis_consistent",
                    "(quantity = 0 AND cost_basis = 0) OR (quantity > 0 AND cost_basis > 0)");
            });
            entity.HasKey(p => p.Id);
            entity.Property(p => p.Id).HasColumnName("id");
            entity.Property(p => p.CompteTradingId)
                .HasColumnName("compte_trading_id");
            entity.Property(p => p.InstrumentId)
                .HasColumnName("instrument_id")
                .HasMaxLength(128)
                .IsRequired();
            entity.Property(p => p.Quantity)
                .HasColumnName("quantity");
            entity.Property(p => p.CostBasis)
                .HasColumnName("cost_basis")
                .HasPrecision(28, 8);
            entity.Property(p => p.RealizedPnl)
                .HasColumnName("realized_pnl")
                .HasPrecision(28, 8);
            entity.Ignore(p => p.AverageAcquisitionCost);
            entity.HasIndex(p => new { p.CompteTradingId, p.InstrumentId })
                .IsUnique();
        });
    }
}
