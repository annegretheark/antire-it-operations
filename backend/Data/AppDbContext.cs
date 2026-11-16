
using Microsoft.EntityFrameworkCore;
using backend.Models;
using backend.Services;

namespace backend.Data;

public class AppDbContext : DbContext
{
    private readonly ITenantContext? _tenant;

    public AppDbContext(
        DbContextOptions<AppDbContext> options,
        ITenantContext? tenant = null)
        : base(options)
    {
        _tenant = tenant;
    }

    // Tenanten til innlogget bruker. Brukes av tenant-filteret under.
    // Uten tenant (null) gir filteret ingen rader.
    public string? CurrentTenantId => _tenant?.TenantId;

    public DbSet<Ticket> Tickets { get; set; }
    public DbSet<Employee> Employees { get; set; }
    public DbSet<AccessRequest> AccessRequests { get; set; }
    public DbSet<EmployeeResponsibility> EmployeeResponsibilities { get; set; }
    public DbSet<TicketCategoryGroup> TicketCategoryGroups { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Tenant-isolasjon: hver tenant ser bare sine egne data
        modelBuilder.Entity<Ticket>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<Employee>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<AccessRequest>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<EmployeeResponsibility>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<TicketCategoryGroup>().HasQueryFilter(e => e.TenantId == CurrentTenantId);

        modelBuilder.Entity<Ticket>().Property(e => e.TenantId).HasMaxLength(36);
        modelBuilder.Entity<Employee>().Property(e => e.TenantId).HasMaxLength(36);
        modelBuilder.Entity<AccessRequest>().Property(e => e.TenantId).HasMaxLength(36);
        modelBuilder.Entity<EmployeeResponsibility>().Property(e => e.TenantId).HasMaxLength(36);
        modelBuilder.Entity<TicketCategoryGroup>().Property(e => e.TenantId).HasMaxLength(36);

        modelBuilder.Entity<Ticket>().HasIndex(t => t.TenantId);
        modelBuilder.Entity<AccessRequest>().HasIndex(r => r.TenantId);

        // Unngå duplikater av Entra-grupper per sakskategori (per tenant)
        modelBuilder.Entity<TicketCategoryGroup>()
            .HasIndex(g => new { g.TenantId, g.Category, g.EntraGroupId })
            .IsUnique();

        // Ansvarsområder for automatisk sakstildeling
        modelBuilder.Entity<EmployeeResponsibility>()
            .HasOne(r => r.Employee)
            .WithMany()
            .HasForeignKey(r => r.EmployeeId)
            .OnDelete(DeleteBehavior.Cascade);

        // Unngå duplikater av samme ansvarsområde
        modelBuilder.Entity<EmployeeResponsibility>()
            .HasIndex(r => new { r.EmployeeId, r.Category })
            .IsUnique();

        // Behold eksisterende relasjon mellom sak og ansatt
        modelBuilder.Entity<Ticket>()
            .HasOne(t => t.ReportedByEmployee)
            .WithMany()
            .HasForeignKey(t => t.ReportedByEmployeeId)
            .OnDelete(DeleteBehavior.Restrict);

        // Ansatt som har fått tildelt saken
        modelBuilder.Entity<Ticket>()
            .HasOne(t => t.AssignedToEmployee)
            .WithMany()
            .HasForeignKey(t => t.AssignedToEmployeeId)
            .OnDelete(DeleteBehavior.Restrict);

        // En Entra-bruker skal bare kunne kobles til én ansatt i en tenant
        modelBuilder.Entity<Employee>()
            .HasIndex(e => new { e.TenantId, e.EntraObjectId })
            .IsUnique()
            .HasFilter("\"EntraObjectId\" IS NOT NULL");

        // Ansattnummer skal være unikt i en tenant
        modelBuilder.Entity<Employee>()
            .HasIndex(e => new { e.TenantId, e.EmployeeNumber })
            .IsUnique()
            .HasFilter("\"EmployeeNumber\" IS NOT NULL");
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ApplyTenant();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        ApplyTenant();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    // Nye rader får alltid innlogget brukers tenant (også om klienten sendte
    // en annen verdi), og eksisterende rader kan ikke flyttes til en annen tenant.
    private void ApplyTenant()
    {
        foreach (var entry in ChangeTracker.Entries<ITenantOwned>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.TenantId = CurrentTenantId
                    ?? throw new InvalidOperationException(
                        "Kan ikke lagre data uten innlogget tenant.");
            }
            else if (entry.State == EntityState.Modified &&
                     entry.Property(e => e.TenantId).IsModified)
            {
                throw new InvalidOperationException("TenantId kan ikke endres.");
            }
        }
    }
}
