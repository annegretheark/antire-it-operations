using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace backend.Migrations
{
    /// <inheritdoc />
    public partial class AddChangeAndRiskManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Løpenumre for CHG-000001 og RISK-000001 (GovernanceNumberService). Lages med
            // SQL, ikke i modellen, slik at SQLite-testene kan opprette databasen.
            migrationBuilder.Sql("CREATE SEQUENCE IF NOT EXISTS change_number_seq START WITH 1;");
            migrationBuilder.Sql("CREATE SEQUENCE IF NOT EXISTS risk_number_seq START WITH 1;");

            migrationBuilder.CreateTable(
                name: "ApprovalRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    ChangeType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Environment = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RequiredRole = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    MinApprovals = table.Column<int>(type: "integer", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalRules", x => x.Id);
                    table.CheckConstraint("CK_ApprovalRules_MinApprovals", "\"MinApprovals\" BETWEEN 1 AND 10");
                });

            migrationBuilder.CreateTable(
                name: "ChangeTemplates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ImplementationPlan = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    TestPlan = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    RollbackPlan = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    AllowedEnvironments = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    MaxRiskScore = table.Column<int>(type: "integer", nullable: false),
                    ApprovedByObjectId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ApprovedByName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NextReviewAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChangeTemplates", x => x.Id);
                    table.CheckConstraint("CK_ChangeTemplates_MaxRiskScore", "\"MaxRiskScore\" BETWEEN 1 AND 25");
                });

            migrationBuilder.CreateTable(
                name: "GovernanceRoleAssignments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    Role = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ServiceCatalogItemId = table.Column<int>(type: "integer", nullable: true),
                    EmployeeId = table.Column<int>(type: "integer", nullable: true),
                    EntraGroupId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GovernanceRoleAssignments", x => x.Id);
                    table.CheckConstraint("CK_GovernanceRoleAssignments_Holder", "(\"EmployeeId\" IS NULL) <> (\"EntraGroupId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_GovernanceRoleAssignments_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GovernanceRoleAssignments_ServiceCatalogItems_ServiceCatalo~",
                        column: x => x.ServiceCatalogItemId,
                        principalTable: "ServiceCatalogItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GovernanceSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    RetentionMonths = table.Column<int>(type: "integer", nullable: true),
                    ReminderDaysBefore = table.Column<int>(type: "integer", nullable: false),
                    SimulatedDeliveryEnabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GovernanceSettings", x => x.Id);
                    table.CheckConstraint("CK_GovernanceSettings_Values", "(\"RetentionMonths\" IS NULL OR \"RetentionMonths\" BETWEEN 1 AND 240) AND \"ReminderDaysBefore\" BETWEEN 0 AND 60");
                });

            migrationBuilder.CreateTable(
                name: "RaciEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    Activity = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Role = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Raci = table.Column<string>(type: "character varying(1)", maxLength: 1, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RaciEntries", x => x.Id);
                    table.CheckConstraint("CK_RaciEntries_Raci", "\"Raci\" IN ('R', 'A', 'C', 'I')");
                });

            migrationBuilder.CreateTable(
                name: "Risks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    Number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    Category = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Likelihood = table.Column<int>(type: "integer", nullable: false),
                    Consequence = table.Column<int>(type: "integer", nullable: false),
                    Score = table.Column<int>(type: "integer", nullable: false, computedColumnSql: "\"Likelihood\" * \"Consequence\"", stored: true),
                    ExistingControls = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    RecommendedActions = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    ResidualLikelihood = table.Column<int>(type: "integer", nullable: true),
                    ResidualConsequence = table.Column<int>(type: "integer", nullable: true),
                    ResidualScore = table.Column<int>(type: "integer", nullable: true, computedColumnSql: "\"ResidualLikelihood\" * \"ResidualConsequence\"", stored: true),
                    Strategy = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    OwnerEmployeeId = table.Column<int>(type: "integer", nullable: true),
                    ActionOwnerEmployeeId = table.Column<int>(type: "integer", nullable: true),
                    DueDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NextReviewAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClosedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Risks", x => x.Id);
                    table.CheckConstraint("CK_Risks_Consequence", "\"Consequence\" BETWEEN 1 AND 5");
                    table.CheckConstraint("CK_Risks_Likelihood", "\"Likelihood\" BETWEEN 1 AND 5");
                    table.CheckConstraint("CK_Risks_ResidualConsequence", "\"ResidualConsequence\" IS NULL OR \"ResidualConsequence\" BETWEEN 1 AND 5");
                    table.CheckConstraint("CK_Risks_ResidualLikelihood", "\"ResidualLikelihood\" IS NULL OR \"ResidualLikelihood\" BETWEEN 1 AND 5");
                    table.ForeignKey(
                        name: "FK_Risks_Employees_ActionOwnerEmployeeId",
                        column: x => x.ActionOwnerEmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Risks_Employees_OwnerEmployeeId",
                        column: x => x.OwnerEmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ChangeRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    Number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    Justification = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ExpectedBenefit = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Priority = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Environment = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Likelihood = table.Column<int>(type: "integer", nullable: true),
                    Impact = table.Column<int>(type: "integer", nullable: true),
                    RiskScore = table.Column<int>(type: "integer", nullable: true, computedColumnSql: "\"Likelihood\" * \"Impact\"", stored: true),
                    RiskAnalysis = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    ImpactAnalysis = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    SecurityImpact = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    GdprImpact = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ProcessingActivityId = table.Column<int>(type: "integer", nullable: true),
                    CostChangePerMonth = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    CostChangeNote = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    BackupStatus = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    BackupNote = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ImplementationPlan = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    TestPlan = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    TestResults = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    RollbackPlan = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    PlannedStart = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PlannedEnd = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RequestedByObjectId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RequestedByName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RequestedByEmployeeId = table.Column<int>(type: "integer", nullable: true),
                    ImplementerEmployeeId = table.Column<int>(type: "integer", nullable: true),
                    ImplementationResult = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    PostReviewRequired = table.Column<bool>(type: "boolean", nullable: false),
                    PostReviewNotes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    PostReviewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PostReviewedByName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    TemplateId = table.Column<int>(type: "integer", nullable: true),
                    PullRequestUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CommitSha = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PipelineRunId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    TerraformPlanHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ScopeHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClosedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChangeRequests", x => x.Id);
                    table.CheckConstraint("CK_ChangeRequests_Impact", "\"Impact\" IS NULL OR \"Impact\" BETWEEN 1 AND 5");
                    table.CheckConstraint("CK_ChangeRequests_Likelihood", "\"Likelihood\" IS NULL OR \"Likelihood\" BETWEEN 1 AND 5");
                    table.ForeignKey(
                        name: "FK_ChangeRequests_ChangeTemplates_TemplateId",
                        column: x => x.TemplateId,
                        principalTable: "ChangeTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ChangeRequests_Employees_ImplementerEmployeeId",
                        column: x => x.ImplementerEmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ChangeRequests_Employees_RequestedByEmployeeId",
                        column: x => x.RequestedByEmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ChangeRequests_ProcessingActivities_ProcessingActivityId",
                        column: x => x.ProcessingActivityId,
                        principalTable: "ProcessingActivities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RiskAcceptances",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    RiskId = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ActorObjectId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ActorName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ActorEmployeeId = table.Column<int>(type: "integer", nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Justification = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ScoreAtDecision = table.Column<int>(type: "integer", nullable: false),
                    ValidUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RiskAcceptances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RiskAcceptances_Employees_ActorEmployeeId",
                        column: x => x.ActorEmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RiskAcceptances_Risks_RiskId",
                        column: x => x.RiskId,
                        principalTable: "Risks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RiskIncidents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    RiskId = table.Column<int>(type: "integer", nullable: false),
                    TicketId = table.Column<int>(type: "integer", nullable: true),
                    AlertId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RiskIncidents", x => x.Id);
                    table.CheckConstraint("CK_RiskIncidents_Target", "(\"TicketId\" IS NULL) <> (\"AlertId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_RiskIncidents_Alerts_AlertId",
                        column: x => x.AlertId,
                        principalTable: "Alerts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RiskIncidents_Risks_RiskId",
                        column: x => x.RiskId,
                        principalTable: "Risks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RiskIncidents_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RiskServices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    RiskId = table.Column<int>(type: "integer", nullable: false),
                    ServiceCatalogItemId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RiskServices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RiskServices_Risks_RiskId",
                        column: x => x.RiskId,
                        principalTable: "Risks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RiskServices_ServiceCatalogItems_ServiceCatalogItemId",
                        column: x => x.ServiceCatalogItemId,
                        principalTable: "ServiceCatalogItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ChangeAffectedServices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    ChangeRequestId = table.Column<int>(type: "integer", nullable: false),
                    ServiceCatalogItemId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChangeAffectedServices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChangeAffectedServices_ChangeRequests_ChangeRequestId",
                        column: x => x.ChangeRequestId,
                        principalTable: "ChangeRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ChangeAffectedServices_ServiceCatalogItems_ServiceCatalogIt~",
                        column: x => x.ServiceCatalogItemId,
                        principalTable: "ServiceCatalogItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ChangeApprovals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    ChangeRequestId = table.Column<int>(type: "integer", nullable: false),
                    Role = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Decision = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ApproverObjectId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ApproverName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ApproverEmployeeId = table.Column<int>(type: "integer", nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Comment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ScopeHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChangeApprovals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChangeApprovals_ChangeRequests_ChangeRequestId",
                        column: x => x.ChangeRequestId,
                        principalTable: "ChangeRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ChangeApprovals_Employees_ApproverEmployeeId",
                        column: x => x.ApproverEmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ChangeAzureScopes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    ChangeRequestId = table.Column<int>(type: "integer", nullable: false),
                    AzureSubscriptionId = table.Column<int>(type: "integer", nullable: false),
                    ResourceGroup = table.Column<string>(type: "character varying(90)", maxLength: 90, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChangeAzureScopes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChangeAzureScopes_AzureSubscriptions_AzureSubscriptionId",
                        column: x => x.AzureSubscriptionId,
                        principalTable: "AzureSubscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ChangeAzureScopes_ChangeRequests_ChangeRequestId",
                        column: x => x.ChangeRequestId,
                        principalTable: "ChangeRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RiskChanges",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    RiskId = table.Column<int>(type: "integer", nullable: false),
                    ChangeRequestId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RiskChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RiskChanges_ChangeRequests_ChangeRequestId",
                        column: x => x.ChangeRequestId,
                        principalTable: "ChangeRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RiskChanges_Risks_RiskId",
                        column: x => x.RiskId,
                        principalTable: "Risks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRules_TenantId_ChangeType_Environment_RequiredRole",
                table: "ApprovalRules",
                columns: new[] { "TenantId", "ChangeType", "Environment", "RequiredRole" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChangeAffectedServices_ChangeRequestId_ServiceCatalogItemId",
                table: "ChangeAffectedServices",
                columns: new[] { "ChangeRequestId", "ServiceCatalogItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChangeAffectedServices_ServiceCatalogItemId",
                table: "ChangeAffectedServices",
                column: "ServiceCatalogItemId");

            migrationBuilder.CreateIndex(
                name: "IX_ChangeApprovals_ApproverEmployeeId",
                table: "ChangeApprovals",
                column: "ApproverEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_ChangeApprovals_ChangeRequestId_DecidedAt",
                table: "ChangeApprovals",
                columns: new[] { "ChangeRequestId", "DecidedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ChangeAzureScopes_AzureSubscriptionId",
                table: "ChangeAzureScopes",
                column: "AzureSubscriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_ChangeAzureScopes_ChangeRequestId_AzureSubscriptionId_Resou~",
                table: "ChangeAzureScopes",
                columns: new[] { "ChangeRequestId", "AzureSubscriptionId", "ResourceGroup" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChangeRequests_ImplementerEmployeeId",
                table: "ChangeRequests",
                column: "ImplementerEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_ChangeRequests_ProcessingActivityId",
                table: "ChangeRequests",
                column: "ProcessingActivityId");

            migrationBuilder.CreateIndex(
                name: "IX_ChangeRequests_RequestedByEmployeeId",
                table: "ChangeRequests",
                column: "RequestedByEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_ChangeRequests_TemplateId",
                table: "ChangeRequests",
                column: "TemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_ChangeRequests_TenantId_Number",
                table: "ChangeRequests",
                columns: new[] { "TenantId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChangeRequests_TenantId_PlannedStart",
                table: "ChangeRequests",
                columns: new[] { "TenantId", "PlannedStart" });

            migrationBuilder.CreateIndex(
                name: "IX_ChangeRequests_TenantId_Status",
                table: "ChangeRequests",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ChangeTemplates_TenantId_Name",
                table: "ChangeTemplates",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GovernanceRoleAssignments_EmployeeId",
                table: "GovernanceRoleAssignments",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_GovernanceRoleAssignments_ServiceCatalogItemId",
                table: "GovernanceRoleAssignments",
                column: "ServiceCatalogItemId");

            migrationBuilder.CreateIndex(
                name: "IX_GovernanceRoleAssignments_TenantId_Role_ServiceCatalogItemId",
                table: "GovernanceRoleAssignments",
                columns: new[] { "TenantId", "Role", "ServiceCatalogItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_GovernanceSettings_TenantId",
                table: "GovernanceSettings",
                column: "TenantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RaciEntries_TenantId_Activity_Role",
                table: "RaciEntries",
                columns: new[] { "TenantId", "Activity", "Role" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RiskAcceptances_ActorEmployeeId",
                table: "RiskAcceptances",
                column: "ActorEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_RiskAcceptances_RiskId_DecidedAt",
                table: "RiskAcceptances",
                columns: new[] { "RiskId", "DecidedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RiskChanges_ChangeRequestId",
                table: "RiskChanges",
                column: "ChangeRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_RiskChanges_RiskId_ChangeRequestId",
                table: "RiskChanges",
                columns: new[] { "RiskId", "ChangeRequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RiskIncidents_AlertId",
                table: "RiskIncidents",
                column: "AlertId");

            migrationBuilder.CreateIndex(
                name: "IX_RiskIncidents_RiskId_TicketId_AlertId",
                table: "RiskIncidents",
                columns: new[] { "RiskId", "TicketId", "AlertId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RiskIncidents_TicketId",
                table: "RiskIncidents",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "IX_Risks_ActionOwnerEmployeeId",
                table: "Risks",
                column: "ActionOwnerEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_Risks_OwnerEmployeeId",
                table: "Risks",
                column: "OwnerEmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_Risks_TenantId_Number",
                table: "Risks",
                columns: new[] { "TenantId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Risks_TenantId_Status",
                table: "Risks",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_RiskServices_RiskId_ServiceCatalogItemId",
                table: "RiskServices",
                columns: new[] { "RiskId", "ServiceCatalogItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RiskServices_ServiceCatalogItemId",
                table: "RiskServices",
                column: "ServiceCatalogItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP SEQUENCE IF EXISTS change_number_seq;");
            migrationBuilder.Sql("DROP SEQUENCE IF EXISTS risk_number_seq;");

            migrationBuilder.DropTable(
                name: "ApprovalRules");

            migrationBuilder.DropTable(
                name: "ChangeAffectedServices");

            migrationBuilder.DropTable(
                name: "ChangeApprovals");

            migrationBuilder.DropTable(
                name: "ChangeAzureScopes");

            migrationBuilder.DropTable(
                name: "GovernanceRoleAssignments");

            migrationBuilder.DropTable(
                name: "GovernanceSettings");

            migrationBuilder.DropTable(
                name: "RaciEntries");

            migrationBuilder.DropTable(
                name: "RiskAcceptances");

            migrationBuilder.DropTable(
                name: "RiskChanges");

            migrationBuilder.DropTable(
                name: "RiskIncidents");

            migrationBuilder.DropTable(
                name: "RiskServices");

            migrationBuilder.DropTable(
                name: "ChangeRequests");

            migrationBuilder.DropTable(
                name: "Risks");

            migrationBuilder.DropTable(
                name: "ChangeTemplates");
        }
    }
}
