using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TestCaseHub.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAutomationIntegration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EnvironmentTargetId",
                table: "TestRuns",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AutomationConfigJson",
                table: "TestCases",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SelectorStability",
                table: "TestCases",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "ApiKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    KeyHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Scope = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Revoked = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EnvironmentTargets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Tenant = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    EnvironmentType = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    DashboardBaseUrl = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    AppApiBaseUrl = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    AppBaseUrl = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    MasterDbConnectionStringEncrypted = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TransactionDbConnectionStringEncrypted = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ReportDbConnectionStringEncrypted = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RequiresTestDataCleanup = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EnvironmentTargets", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_KeyHash",
                table: "ApiKeys",
                column: "KeyHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApiKeys");

            migrationBuilder.DropTable(
                name: "EnvironmentTargets");

            migrationBuilder.DropColumn(
                name: "EnvironmentTargetId",
                table: "TestRuns");

            migrationBuilder.DropColumn(
                name: "AutomationConfigJson",
                table: "TestCases");

            migrationBuilder.DropColumn(
                name: "SelectorStability",
                table: "TestCases");
        }
    }
}
