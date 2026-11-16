
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
builder.Services.AddScoped<ITenantContext, HttpTenantContext>();
// Singleton: gjenbruker credential og token-cache på tvers av forespørsler
builder.Services.AddSingleton<backend.Services.GraphService>();
builder.Services.AddDbContext<backend.Data.AppDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("DefaultConnection")));

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
builder.Services.AddControllers();
builder.Services.AddScoped<TicketAssignmentService>();
builder.Services.AddScoped<EmployeeNumberService>();
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

    options.AddPolicy(AuthPolicies.EmployeeAdmin,
        policy => policy.RequireRole(AppRoles.EmployeeSyncAdmin));

    // TODO: Egen lederrolle når den er opprettet i app-registreringen
    options.AddPolicy(AuthPolicies.AccessApprover,
        policy => policy.RequireRole(AppRoles.HelpdeskAdmin));
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
