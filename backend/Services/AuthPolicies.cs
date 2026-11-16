namespace backend.Services;

// App-roller definert i app-registreringen i Entra ID
public static class AppRoles
{
    public const string HelpdeskAdmin = "Helpdesk.Admin";
    public const string EmployeeSyncAdmin = "EmployeeSync.Admin";
}

// Navngitte tilgangsregler brukt i [Authorize(Policy = ...)]
public static class AuthPolicies
{
    public const string HelpdeskAdmin = "HelpdeskAdmin";
    public const string EmployeeAdmin = "EmployeeAdmin";
    public const string AccessApprover = "AccessApprover";
}
