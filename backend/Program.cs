
using Microsoft.Identity.Web;
using Microsoft.EntityFrameworkCore;
using backend.Services;

var builder = WebApplication.CreateBuilder(args);
// Singleton: gjenbruker credential og token-cache på tvers av forespørsler
builder.Services.AddSingleton<backend.Services.GraphService>();
builder.Services.AddDbContext<backend.Data.AppDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("DefaultConnection")));

// CORS for React frontend
builder.Services.AddCors(options =>
{
    options.AddPolicy("ReactDev", policy =>
    {
        policy.WithOrigins("http://localhost:5173")
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

app.UseCors("ReactDev");

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers().RequireAuthorization();
app.Run();
