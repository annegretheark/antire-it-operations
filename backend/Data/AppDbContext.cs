
using Microsoft.EntityFrameworkCore;
using backend.Models;

namespace backend.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<Ticket> Tickets { get; set; }
    public DbSet<Employee> Employees { get; set; }
    public DbSet<AccessRequest> AccessRequests { get; set; }
    public DbSet<EmployeeResponsibility> EmployeeResponsibilities { get; set; }
    public DbSet<TicketCategoryGroup> TicketCategoryGroups { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
                // Unngå duplikater av Entra-grupper per sakskategori
        modelBuilder.Entity<TicketCategoryGroup>()
            .HasIndex(g => new { g.Category, g.EntraGroupId })
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

        // En Entra-bruker skal bare kunne kobles til én ansatt
        modelBuilder.Entity<Employee>()
            .HasIndex(e => e.EntraObjectId)
            .IsUnique()
            .HasFilter("\"EntraObjectId\" IS NOT NULL");
            // Ansattnummer skal være unikt
        modelBuilder.Entity<Employee>()
            .HasIndex(e => e.EmployeeNumber)
            .IsUnique()
            .HasFilter("\"EmployeeNumber\" IS NOT NULL");
    }
}
