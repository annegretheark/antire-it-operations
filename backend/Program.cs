
using Microsoft.Identity.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using backend.Services;

var builder = WebApplication.CreateBuilder(args);

// Tenanter appen kan brukes i (f.eks. test og prod). Data holdes adskilt per tenant.
builder.Services.Configure<TenantOptions>(
    builder.Configuration.GetSection(TenantOptions.Section));
var tenantOptions = builder.Configuration
    .GetSection(TenantOptions.Section)
    .Get<TenantOptions>() ?? new TenantOptions();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<HttpTenantContext>();
builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<HttpTenantContext>());
// Innlogget person (brukes bl.a. av revisjonsloggen)
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
// Revisjonslogg: logger endringer på IAudited-entiteter ved lagring
builder.Services.AddScoped<backend.Data.AuditInterceptor>();
// Singleton: gjenbruker credential og token-cache på tvers av forespørsler
builder.Services.AddSingleton<backend.Services.GraphService>();
// Azure Resource Manager og Resource Graph (fase 3), samme app-registrering som Graph
builder.Services.AddHttpClient(nameof(AzureService));
builder.Services.AddSingleton<AzureService>();
// Teams-varsling (fase 4.4); webhook-adressen krypteres med Data Protection
builder.Services.AddHttpClient(nameof(TeamsService));
builder.Services.AddSingleton<TeamsService>();
builder.Services.AddDataProtection();
builder.Services.AddDbContext<backend.Data.AppDbContext>((services, options) =>
    options
        .UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"))
        .AddInterceptors(services.GetRequiredService<backend.Data.AuditInterceptor>()));

// CORS for React frontend: tillatte adresser fra konfigurasjon (Cors:AllowedOrigins)
var allowedOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>() ?? [];

builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
    {
        policy.WithOrigins(allowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

// API Controllers
// Tidspunkter i forespørsler lagres som UTC (se UtcDateTimeConverter)
builder.Services.AddControllers()
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new UtcDateTimeConverter()));
builder.Services.AddScoped<TicketAssignmentService>();
builder.Services.AddScoped<EmployeeNumberService>();
builder.Services.AddScoped<ServiceCatalogService>();
builder.Services.AddScoped<SlaService>();
builder.Services.AddScoped<AssetService>();
builder.Services.AddScoped<AccessGrantService>();
// Varsler blir saker (fase 4)
builder.Services.AddScoped<TeamsNotificationService>();
builder.Services.AddScoped<AlertRuleService>();
builder.Services.AddScoped<AlertIngestionService>();
builder.Services.AddSingleton<AlertSourceStatusRegistry>();
builder.Services.AddSingleton<AlertMonitorService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AlertMonitorService>());
// Kort cache for Microsoft 365-oversiktene (M365Controller)
builder.Services.AddMemoryCache();
// Markerer SLA-brudd hvert 5. minutt (Sla:MonitorEnabled, Sla:CheckIntervalSeconds)
builder.Services.AddSingleton<SlaMonitorService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SlaMonitorService>());
// Microsoft Entra ID - autentisering av API
builder.Services
    .AddAuthentication("Bearer")
    .AddMicrosoftIdentityWebApi(
        builder.Configuration.GetSection("AzureAd"),
        jwtBearerScheme: "Bearer");

// Tilgangsregler basert på app-roller i Entra ID
builder.Services.AddAuthorization(options =>
{
    // Alle endepunkter krever innlogging fra en tillatt tenant
    options.DefaultPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireAssertion(context =>
            tenantOptions.IsAllowed(context.User.GetTenantId()))
        .Build();

    options.AddPolicy(AuthPolicies.HelpdeskAdmin,
        policy => policy.RequireRole(AppRoles.HelpdeskAdmin));

    options.AddPolicy(AuthPolicies.HelpdeskAgent,
        policy => policy.RequireRole(AppRoles.HelpdeskAgent, AppRoles.HelpdeskAdmin));

    options.AddPolicy(AuthPolicies.AccessApprover,
        policy => policy.RequireRole(AppRoles.Approver, AppRoles.HelpdeskAdmin));

    options.AddPolicy(AuthPolicies.EmployeeAdmin,
        policy => policy.RequireRole(AppRoles.EmployeeSyncAdmin));
});

// OpenAPI
builder.Services.AddOpenApi();

var app = builder.Build();

// Development
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// HTTP pipeline
app.UseHttpsRedirection();

app.UseCors("Frontend");

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers().RequireAuthorization();
app.Run();
public partial class Program { }
