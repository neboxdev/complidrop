using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CompliDrop.Api.Migrations
{
    /// <summary>
    /// Renames legacy <c>Subscriptions.Plan = 'monthly'</c> rows to
    /// <c>'pro'</c> per ADR 0011. The application-side vocab is unified
    /// as <c>free | pro | annual | founding</c>; the Stripe-side
    /// config-key names (<c>MonthlyPriceId</c> / <c>AnnualPriceId</c> /
    /// <c>FoundingPriceId</c>) stay billing-cadence words and are
    /// translated in <c>StripeService.ResolvePlanFromPriceId</c>.
    ///
    /// Idempotent: a second run is a no-op (no rows remain with
    /// <c>Plan = 'monthly'</c> after the first run). Reversible via
    /// <c>Down</c> in case of a rollback during deploy.
    ///
    /// Uses bare string equality on a non-timestamptz column, so
    /// ADR 0009 doesn't apply.
    /// </summary>
    public partial class RenameSubscriptionPlanMonthlyToPro : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE \"Subscriptions\" SET \"Plan\" = 'pro' WHERE \"Plan\" = 'monthly';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse the rename. Note: this is not strictly invertible —
            // if a row was created with Plan = 'pro' *after* this migration
            // ran (the expected forward state), Down() can't tell that
            // apart from a row that was originally 'monthly'. The reversal
            // is a best-effort restore for an immediate post-deploy
            // rollback, not a long-lived undo path.
            migrationBuilder.Sql("UPDATE \"Subscriptions\" SET \"Plan\" = 'monthly' WHERE \"Plan\" = 'pro';");
        }
    }
}
