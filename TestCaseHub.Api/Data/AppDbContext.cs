using Microsoft.EntityFrameworkCore;
using TestCaseHub.Api.Models;

namespace TestCaseHub.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Module> Modules => Set<Module>();
    public DbSet<TaskLink> TaskLinks => Set<TaskLink>();
    public DbSet<TestCase> TestCases => Set<TestCase>();
    public DbSet<TestCaseHistory> History => Set<TestCaseHistory>();
    public DbSet<PriorityOption> Priorities => Set<PriorityOption>();
    public DbSet<StatusOption> Statuses => Set<StatusOption>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<InviteLink> InviteLinks => Set<InviteLink>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<TestSuite> TestSuites => Set<TestSuite>();
    public DbSet<TestCaseComment> Comments => Set<TestCaseComment>();
    public DbSet<Release> Releases => Set<Release>();
    public DbSet<TestRun> TestRuns => Set<TestRun>();
    public DbSet<TestRunResult> TestRunResults => Set<TestRunResult>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<EnvironmentTarget> EnvironmentTargets => Set<EnvironmentTarget>();
    public DbSet<EnvironmentCredential> EnvironmentCredentials => Set<EnvironmentCredential>();
    public DbSet<ModuleRepoLink> ModuleRepoLinks => Set<ModuleRepoLink>();
    public DbSet<AutomationScript> AutomationScripts => Set<AutomationScript>();

    // Phase 8: multi-company, teams.
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<CompanyAdminInvite> CompanyAdminInvites => Set<CompanyAdminInvite>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<TeamMember> TeamMembers => Set<TeamMember>();
    public DbSet<TeamModule> TeamModules => Set<TeamModule>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>().HasIndex(u => u.Email).IsUnique();
        // Explicit DB-level defaults for the RBAC columns — without these, EF scaffolds the
        // CLR default (false/"") into the migration's AddColumn instead of the property
        // initializer's actual default (true/"Viewer"), which would silently deactivate or
        // mis-role every EXISTING user the moment this migration runs against real data.
        modelBuilder.Entity<User>().Property(u => u.IsActive).HasDefaultValue(true);
        modelBuilder.Entity<User>().Property(u => u.Role).HasDefaultValue(Models.Roles.Viewer);
        // Phase 8: Module.Code used to be globally unique; now it only needs to be unique
        // WITHIN a company (two different companies are free to both have a "CHK" module).
        modelBuilder.Entity<Module>().HasIndex(m => new { m.CompanyId, m.Code }).IsUnique();
        modelBuilder.Entity<PriorityOption>().HasIndex(p => p.Value).IsUnique();
        modelBuilder.Entity<StatusOption>().HasIndex(s => s.Value).IsUnique();
        modelBuilder.Entity<InviteLink>().HasIndex(i => i.Code).IsUnique();
        modelBuilder.Entity<CompanyAdminInvite>().HasIndex(i => i.Code).IsUnique();
        modelBuilder.Entity<RefreshToken>().HasIndex(r => r.TokenHash).IsUnique();
        // Filtered so only NON-NULL RunAttemptKey values must be unique — SQL Server's default
        // unique-index behaviour otherwise only tolerates a single NULL total, which would
        // break the moment a second manual (non-automated, no attempt key) result was recorded.
        // SQL Server's filtered-index syntax ("[Col] IS NOT NULL") isn't valid Postgres, which
        // wants a plain quoted-identifier expression instead -- Npgsql throws a syntax error at
        // deploy time otherwise, so pick the right dialect for whichever provider is active.
        var runAttemptKeyFilter = Database.ProviderName?.Contains("Npgsql") == true
            ? "\"RunAttemptKey\" IS NOT NULL"
            : "[RunAttemptKey] IS NOT NULL";
        modelBuilder.Entity<TestRunResult>().HasIndex(r => r.RunAttemptKey).IsUnique().HasFilter(runAttemptKeyFilter);
        modelBuilder.Entity<ApiKey>().HasIndex(k => k.KeyHash).IsUnique();

        // EnvironmentCredential is a child collection of EnvironmentTarget (Phase: automation
        // credentials) -- deleting an environment target takes its named logins with it.
        modelBuilder.Entity<EnvironmentCredential>()
            .HasOne<EnvironmentTarget>().WithMany().HasForeignKey(c => c.EnvironmentTargetId).OnDelete(DeleteBehavior.Cascade);

        // A module can have several repo links (one per layer: Frontend/Backend/Database) but
        // never two for the exact same layer -- that would just be an edit, not a new link.
        modelBuilder.Entity<ModuleRepoLink>().HasIndex(l => new { l.ModuleId, l.Layer }).IsUnique();
        modelBuilder.Entity<ModuleRepoLink>()
            .HasOne<Module>().WithMany().HasForeignKey(l => l.ModuleId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<PasswordResetToken>().HasIndex(r => r.TokenHash).IsUnique();

        modelBuilder.Entity<Module>()
            .HasMany(m => m.TaskLinks)
            .WithOne(t => t.Module)
            .HasForeignKey(t => t.ModuleId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Module>()
            .HasMany(m => m.TestCases)
            .WithOne(t => t.Module)
            .HasForeignKey(t => t.ModuleId)
            .OnDelete(DeleteBehavior.Restrict);

        // Phase 8: Team<->User and Team<->Module are plain many-to-many join rows (not EF's
        // implicit skip-navigation many-to-many) so each membership/assignment carries its own
        // Id + timestamp — simpler to query/report on than a shadow join table would be.
        modelBuilder.Entity<TeamMember>().HasIndex(tm => new { tm.TeamId, tm.UserId }).IsUnique();
        modelBuilder.Entity<TeamModule>().HasIndex(tm => new { tm.TeamId, tm.ModuleId }).IsUnique();
        modelBuilder.Entity<TeamMember>()
            .HasOne(tm => tm.Team).WithMany().HasForeignKey(tm => tm.TeamId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<TeamMember>()
            .HasOne(tm => tm.User).WithMany().HasForeignKey(tm => tm.UserId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<TeamModule>()
            .HasOne(tm => tm.Team).WithMany().HasForeignKey(tm => tm.TeamId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<TeamModule>()
            .HasOne(tm => tm.Module).WithMany().HasForeignKey(tm => tm.ModuleId).OnDelete(DeleteBehavior.Cascade);

        // Seed the base Priority/Status values (mirrors PRIORITIES/STATUSES constants in the artifact).
        modelBuilder.Entity<PriorityOption>().HasData(
            new PriorityOption { Id = 1, Value = "P1", IsCustom = false },
            new PriorityOption { Id = 2, Value = "P2", IsCustom = false },
            new PriorityOption { Id = 3, Value = "P3", IsCustom = false },
            new PriorityOption { Id = 4, Value = "P4", IsCustom = false }
        );
        modelBuilder.Entity<StatusOption>().HasData(
            new StatusOption { Id = 1, Value = "Draft", IsCustom = false },
            new StatusOption { Id = 2, Value = "Reviewed", IsCustom = false },
            new StatusOption { Id = 3, Value = "Active", IsCustom = false },
            new StatusOption { Id = 4, Value = "Deprecated", IsCustom = false }
        );
    }
}
