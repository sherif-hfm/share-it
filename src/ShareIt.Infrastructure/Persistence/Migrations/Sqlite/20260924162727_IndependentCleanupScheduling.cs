using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ShareIt.Infrastructure.Persistence.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class IndependentCleanupScheduling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Older versions stored session expiry here, including after failed
            // deletions. Only real retry backoff (at most one hour) should survive.
            migrationBuilder.Sql("""
                UPDATE "CleanupTask"
                SET "NextAttemptAtUtc" = CURRENT_TIMESTAMP
                WHERE "Attempts" = 0
                   OR "NextAttemptAtUtc" > datetime('now', '+1 hour');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Normalizing work eligibility does not remove session or file data.
        }
    }
}
