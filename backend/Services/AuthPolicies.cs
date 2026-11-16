using System.Security.Claims;

namespace backend.Services;

// App-roller definert i app-registreringen i Entra ID (opprettes med
// scripts/Set-AppRoles.ps1). Tildeles brukere i hver tenant.
public static class AppRoles
{
    // Konfigurasjon og alt i helpdesk
    public const string HelpdeskAdmin = "Helpdesk.Admin";

    // Behandle saker og tilgangsbestillinger, men ikke konfigurasjon
    public const string HelpdeskAgent = "Helpdesk.Agent";

    // Godkjenne eller avslå tilgangsbestillinger
    public const string Approver = "Approver";

    // Synk og vedlikehold av ansatte
    public const string EmployeeSyncAdmin = "EmployeeSync.Admin";
}

// Navngitte tilgangsregler brukt i [Authorize(Policy = ...)]
public static class AuthPolicies
{
    // Bare Helpdesk.Admin: konfigurasjon (kategorigrupper, senere katalog og SLA)
    public const string HelpdeskAdmin = "HelpdeskAdmin";

    // Helpdesk.Agent eller Helpdesk.Admin: behandle saker og bestillinger
    public const string HelpdeskAgent = "HelpdeskAgent";

    // Approver eller Helpdesk.Admin: godkjenne tilgangsbestillinger
    public const string AccessApprover = "AccessApprover";

    // EmployeeSync.Admin: ansatte og synk fra Entra
    public const string EmployeeAdmin = "EmployeeAdmin";
}

public static class RoleChecks
{
    // Kan behandle saker og se alle saker og bestillinger i tenanten
    public static bool IsHelpdeskAgent(this ClaimsPrincipal user) =>
        user.IsInRole(AppRoles.HelpdeskAgent) || user.IsInRole(AppRoles.HelpdeskAdmin);

    // Kan godkjenne tilgangsbestillinger
    public static bool IsAccessApprover(this ClaimsPrincipal user) =>
        user.IsInRole(AppRoles.Approver) || user.IsInRole(AppRoles.HelpdeskAdmin);
}
