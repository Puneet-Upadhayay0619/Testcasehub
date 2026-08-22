using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TestCaseHub.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTestRunCredential : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EnvironmentCredentialId",
                table: "TestRuns",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EnvironmentCredentialId",
                table: "TestRuns");
        }
    }
}
