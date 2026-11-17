
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using backend.Models;
using backend.Services;

namespace backend.Data;

public class AppDbContext : DbContext
{
    private readonly ITenantContext? _tenant;

    // Forretningshendelse for en entitet i neste lagring (f.eks. approved i stedet
    // for updated). Leses av AuditInterceptor.
    private readonly Dictionary<object, string> _auditActions =
        new(ReferenceEqualityComparer.Instance);

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
    public DbSet<AuditEvent> AuditEvents { get; set; }
    public DbSet<ServiceCatalogItem> ServiceCatalogItems { get; set; }
    public DbSet<SlaTarget> SlaTargets { get; set; }
    public DbSet<Asset> Assets { get; set; }
    public DbSet<AzureSubscription> AzureSubscriptions { get; set; }
    public DbSet<Alert> Alerts { get; set; }
    public DbSet<AlertRule> AlertRules { get; set; }
    public DbSet<TeamsSettings> TeamsSettings { get; set; }
    public DbSet<TeamsNotification> TeamsNotifications { get; set; }
    public DbSet<LifecycleTemplateTask> LifecycleTemplateTasks { get; set; }
    public DbSet<LifecycleProcess> LifecycleProcesses { get; set; }
    public DbSet<LifecycleTask> LifecycleTasks { get; set; }
    public DbSet<ProcessingActivity> ProcessingActivities { get; set; }
    public DbSet<GdprSettings> GdprSettings { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Tenant-isolasjon: hver tenant ser bare sine egne data
        modelBuilder.Entity<Ticket>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<Employee>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<AccessRequest>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<EmployeeResponsibility>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<TicketCategoryGroup>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<AuditEvent>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<ServiceCatalogItem>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<SlaTarget>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<Asset>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<AzureSubscription>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<Alert>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<AlertRule>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<TeamsSettings>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<TeamsNotification>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<LifecycleTemplateTask>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<LifecycleProcess>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<LifecycleTask>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<ProcessingActivity>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        modelBuilder.Entity<GdprSettings>().HasQueryFilter(e => e.TenantId == CurrentTenantId);

        // Revisjonslogg: historikk per entitet og siste hendelser i tenanten
        modelBuilder.Entity<AuditEvent>(audit =>
        {
            audit.Property(e => e.TenantId).HasMaxLength(36);
            audit.Property(e => e.ActorObjectId).HasMaxLength(64);
            audit.Property(e => e.ActorName).HasMaxLength(256);
            audit.Property(e => e.EntityType).HasMaxLength(64);
            audit.Property(e => e.EntityId).HasMaxLength(64);
            audit.Property(e => e.Action).HasMaxLength(32);
            audit.Property(e => e.Changes).HasColumnType("jsonb");
            audit.HasIndex(e => new { e.TenantId, e.EntityType, e.EntityId });
            audit.HasIndex(e => new { e.TenantId, e.OccurredAt });
        });

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

        // Onboarding og offboarding: bare én aktiv prosess av hver type per ansatt
        modelBuilder.Entity<LifecycleTemplateTask>(task =>
        {
            task.Property(e => e.TenantId).HasMaxLength(36);
            task.Property(e => e.Type).HasMaxLength(20);
            task.Property(e => e.Title).HasMaxLength(200);
            task.Property(e => e.Description).HasMaxLength(1000);
            task.Property(e => e.Category).HasMaxLength(100);
        });

        modelBuilder.Entity<LifecycleProcess>(process =>
        {
            process.Property(e => e.TenantId).HasMaxLength(36);
            process.Property(e => e.Type).HasMaxLength(20);
            process.Property(e => e.Status).HasMaxLength(20);
            process.HasOne(e => e.Employee)
                .WithMany()
                .HasForeignKey(e => e.EmployeeId)
                .OnDelete(DeleteBehavior.Restrict);
            process.HasMany(e => e.Tasks)
                .WithOne()
                .HasForeignKey(t => t.ProcessId)
                .OnDelete(DeleteBehavior.Cascade);
            process.HasIndex(e => new { e.TenantId, e.EmployeeId, e.Type })
                .IsUnique()
                .HasFilter("\"Status\" = 'active'");
        });

        modelBuilder.Entity<LifecycleTask>(task =>
        {
            task.Property(e => e.TenantId).HasMaxLength(36);
            task.Property(e => e.Title).HasMaxLength(200);
            task.Property(e => e.Description).HasMaxLength(1000);
            task.Property(e => e.Category).HasMaxLength(100);
            task.Property(e => e.CompletedByName).HasMaxLength(256);
            task.Property(e => e.Note).HasMaxLength(1000);
        });

        // GDPR: behandlingsprotokoll og innstillinger
        modelBuilder.Entity<ProcessingActivity>(activity =>
        {
            activity.Property(e => e.TenantId).HasMaxLength(36);
            activity.Property(e => e.Name).HasMaxLength(200);
            activity.Property(e => e.System).HasMaxLength(200);
            activity.Property(e => e.LegalBasis).HasMaxLength(30);
            activity.Property(e => e.Owner).HasMaxLength(200);
        });

        modelBuilder.Entity<GdprSettings>(settings =>
        {
            settings.Property(e => e.TenantId).HasMaxLength(36);
            settings.HasIndex(e => e.TenantId).IsUnique();
        });

        // Varsler (fase 4): samme varsel fra samme kilde lagres én gang per tenant.
        // Saken peker på varselet med fremmednøkkel; Alert.TicketId er bare en
        // kolonne (fremmednøkkel begge veier ville hindret lagring i én omgang).
        modelBuilder.Entity<Alert>(alert =>
        {
            alert.Property(e => e.TenantId).HasMaxLength(36);
            alert.Property(e => e.Source).HasMaxLength(20);
            alert.Property(e => e.ExternalId).HasMaxLength(500);
            alert.Property(e => e.Title).HasMaxLength(500);
            alert.Property(e => e.Severity).HasMaxLength(20);
            alert.Property(e => e.ResourceId).HasMaxLength(1000);
            alert.Property(e => e.PortalUrl).HasMaxLength(2000);
            alert.Property(e => e.State).HasMaxLength(20);
            alert.Property(e => e.RawPayload).HasColumnType("jsonb");
            alert.HasIndex(e => new { e.TenantId, e.Source, e.ExternalId }).IsUnique();
            alert.HasIndex(e => e.TicketId);
        });

        modelBuilder.Entity<Ticket>(ticket =>
        {
            ticket.Property(e => e.Source).HasMaxLength(20);
            ticket.HasOne<Alert>()
                .WithMany()
                .HasForeignKey(t => t.AlertId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AlertRule>(rule =>
        {
            rule.Property(e => e.TenantId).HasMaxLength(36);
            rule.Property(e => e.Source).HasMaxLength(20);
            rule.Property(e => e.MinSeverity).HasMaxLength(20);
            rule.Property(e => e.Category).HasMaxLength(100);
            rule.Property(e => e.PriorityOverride).HasMaxLength(20);
            rule.HasIndex(e => new { e.TenantId, e.Source }).IsUnique();
        });

        modelBuilder.Entity<TeamsSettings>(settings =>
        {
            settings.Property(e => e.TenantId).HasMaxLength(36);
            settings.HasIndex(e => e.TenantId).IsUnique();
        });

        modelBuilder.Entity<TeamsNotification>(notification =>
        {
            notification.Property(e => e.TenantId).HasMaxLength(36);
            notification.Property(e => e.Kind).HasMaxLength(30);
            notification.Property(e => e.Status).HasMaxLength(20);
            notification.Property(e => e.Payload).HasColumnType("jsonb");
            notification.Property(e => e.LastError).HasMaxLength(1000);
            notification.HasIndex(e => new { e.TenantId, e.Status });
        });

        // Azure-abonnementer: abonnements-ID-en er unik i tenanten
        modelBuilder.Entity<AzureSubscription>(subscription =>
        {
            subscription.Property(e => e.TenantId).HasMaxLength(36);
            subscription.Property(e => e.SubscriptionId).HasMaxLength(36);
            subscription.Property(e => e.DisplayName).HasMaxLength(200);
            subscription.Property(e => e.State).HasMaxLength(20);
            subscription.HasIndex(e => new { e.TenantId, e.SubscriptionId }).IsUnique();
        });

        // IT-utstyr: utstyrsnummeret er unikt i tenanten. Utstyr og ansatte slettes
        // ikke så lenge noe peker på dem.
        modelBuilder.Entity<Asset>(asset =>
        {
            asset.Property(e => e.TenantId).HasMaxLength(36);
            asset.Property(e => e.AssetTag).HasMaxLength(50);
            asset.Property(e => e.Type).HasMaxLength(20);
            asset.Property(e => e.Manufacturer).HasMaxLength(100);
            asset.Property(e => e.Model).HasMaxLength(100);
            asset.Property(e => e.SerialNumber).HasMaxLength(100);
            asset.Property(e => e.Status).HasMaxLength(20);
            asset.Property(e => e.Source).HasMaxLength(20);
            asset.Property(e => e.ExternalId).HasMaxLength(100);
            asset.Property(e => e.Notes).HasMaxLength(2000);
            asset.HasIndex(e => new { e.TenantId, e.AssetTag }).IsUnique();
            asset.HasOne(e => e.AssignedToEmployee)
                .WithMany()
                .HasForeignKey(e => e.AssignedToEmployeeId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Ticket>()
            .HasOne<Asset>()
            .WithMany()
            .HasForeignKey(t => t.AssetId)
            .OnDelete(DeleteBehavior.Restrict);

        // SLA-mål: ett per prioritet i tenanten
        modelBuilder.Entity<SlaTarget>(target =>
        {
            target.Property(e => e.TenantId).HasMaxLength(36);
            target.Property(e => e.Priority).HasMaxLength(20);
            target.HasIndex(e => new { e.TenantId, e.Priority }).IsUnique();
        });

        // Tjenestekatalog: navnet er unikt i tenanten. Saker og bestillinger peker hit,
        // og en tjeneste kan ikke slettes så lenge noe peker på den (deaktiveres i stedet).
        modelBuilder.Entity<ServiceCatalogItem>(item =>
        {
            item.Property(e => e.TenantId).HasMaxLength(36);
            item.Property(e => e.Name).HasMaxLength(100);
            item.Property(e => e.Category).HasMaxLength(100);
            item.Property(e => e.Description).HasMaxLength(500);
            item.Property(e => e.Type).HasMaxLength(20);
            item.Property(e => e.DefaultPriority).HasMaxLength(20);
            item.Property(e => e.EntraGroupId).HasMaxLength(36);
            item.HasIndex(e => new { e.TenantId, e.Name }).IsUnique();
        });

        modelBuilder.Entity<Ticket>()
            .HasOne<ServiceCatalogItem>()
            .WithMany()
            .HasForeignKey(t => t.ServiceCatalogItemId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<AccessRequest>()
            .HasOne<Employee>()
            .WithMany()
            .HasForeignKey(r => r.EmployeeId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<AccessRequest>()
            .HasOne<ServiceCatalogItem>()
            .WithMany()
            .HasForeignKey(r => r.ServiceCatalogItemId)
            .OnDelete(DeleteBehavior.Restrict);

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

    // Merk neste lagring av entiteten med en forretningshendelse, f.eks.
    // SetAuditAction(request, AuditActions.Approved). Uten dette brukes updated.
    public void SetAuditAction(object entity, string action) =>
        _auditActions[entity] = action;

    internal bool TryTakeAuditAction(object entity, [NotNullWhen(true)] out string? action) =>
        _auditActions.Remove(entity, out action);

    // Logg en hendelse som ikke er en endring på én rad, f.eks. synk fra Entra.
    // Lagres med neste SaveChanges; AuditInterceptor fyller inn hvem og når.
    public void AddAuditEvent(
        string entityType, string entityId, string action, object? details = null)
    {
        AuditEvents.Add(new AuditEvent
        {
            EntityType = entityType,
            EntityId = entityId,
            Action = action,
            Changes = details == null
                ? null
                : JsonSerializer.Serialize(details, AuditInterceptor.JsonOptions),
        });
    }

    // Revisjonsloggen føres av AuditInterceptor (registreres i Program.cs)
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
