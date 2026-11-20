
using Microsoft.EntityFrameworkCore;
using QuantDesk.Modules.Administration.Domain.Entities;

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
        });
    }
}
