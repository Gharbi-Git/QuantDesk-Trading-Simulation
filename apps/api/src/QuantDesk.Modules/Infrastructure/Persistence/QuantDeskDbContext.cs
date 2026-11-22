
using Microsoft.EntityFrameworkCore;
using QuantDesk.Modules.Administration.Domain.Entities;
using QuantDesk.Modules.Portfolio.Domain.Entities;
namespace QuantDesk.Modules.Infrastructure.Persistence;

public sealed class QuantDeskDbContext : DbContext
{
    public QuantDeskDbContext(
        DbContextOptions<QuantDeskDbContext> options)
        : base(options)
    {
    }

    public DbSet<Utilisateur> Utilisateurs =>
        Set<Utilisateur>();

    public DbSet<CompteTrading> ComptesTrading =>
    Set<CompteTrading>();

public DbSet<Portefeuille> Portefeuilles =>
    Set<Portefeuille>();

public DbSet<CashJournalEntry> CashJournal =>
    Set<CashJournalEntry>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Utilisateur>(entity =>
        {
            entity.ToTable("utilisateurs");

            entity.HasKey(user => user.Id);

            entity.Property(user => user.Id)
                .HasColumnName("id");

            entity.Property(user => user.KeycloakSubject)
                .HasColumnName("keycloak_subject")
                .HasMaxLength(255)
                .IsRequired();

            entity.HasIndex(user => user.KeycloakSubject)
                .IsUnique(); //ensures PostgreSQL cannot store two QuantDesk user records with the same Keycloak subject.

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

    // Only one personal account per player.
    // Other account types may be multiple in future.
    entity.HasIndex(a => a.OwnerUserId)
        .IsUnique()
        .HasFilter("account_type = 'Personal'");

    entity.HasOne<
        QuantDesk.Modules.Administration.Domain.Entities.Utilisateur>()
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

    entity.HasIndex(j => new
    {
        j.CompteTradingId,
        j.EventKey
    }).IsUnique();
});

        });

    }
}
