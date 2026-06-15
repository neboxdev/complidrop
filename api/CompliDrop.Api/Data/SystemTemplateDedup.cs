namespace CompliDrop.Api.Data;

/// <summary>
/// One-time data cleanup for #251: prod accumulated TWO live rows per system-template venue type
/// (a #192 seed-vs-rename mismatch in the manual-migration era). For each duplicated name this keeps
/// a single survivor — preferring the copy the most vendors reference, then the oldest, then the
/// lowest id (fully deterministic) — repoints every vendor off the dropped copies onto the survivor,
/// removes the dropped copies' orphaned <c>ComplianceChecks</c> (that FK is ON DELETE RESTRICT, so it
/// must go before the rules), and deletes the dropped templates (cascading their rules). The
/// repointed documents keep their cached verdict; their checks regenerate on the next evaluation
/// against the survivor's identical rules.
///
/// It is a NO-OP when there are no duplicates (e.g. a fresh database where the seed has inserted one
/// row per name), so it is safe to run via migration on every environment. Exposed as a const so the
/// migration executes it AND the regression test can drive it against a seeded-with-duplicates DB.
/// Bare <c>now()</c> on the timestamptz <c>UpdatedAt</c> — never AT TIME ZONE (ADR 0009).
///
/// FROZEN once shipped: a migration that has already run will not re-apply, so do not change this
/// SQL's behavior after merge (it would only affect never-migrated environments, where it is a
/// no-op anyway).
/// </summary>
public static class SystemTemplateDedup
{
    public const string DedupeSql = """
        CREATE TEMP TABLE _complidrop_sys_tpl_dupes ON COMMIT DROP AS
        WITH refs AS (
            SELECT t."Id", t."Name", t."CreatedAt",
                   (SELECT count(*) FROM "Vendors" v WHERE v."ComplianceTemplateId" = t."Id") AS ref_count
            FROM "ComplianceTemplates" t
            WHERE t."IsSystemTemplate" AND t."DeletedAt" IS NULL
        ),
        ranked AS (
            SELECT "Id", "Name",
                   ROW_NUMBER() OVER w AS rn,
                   first_value("Id") OVER w AS survivor_id
            FROM refs
            WINDOW w AS (PARTITION BY "Name" ORDER BY ref_count DESC, "CreatedAt" ASC, "Id" ASC)
        )
        SELECT "Id" AS dupe_id, survivor_id FROM ranked WHERE rn > 1;

        DELETE FROM "ComplianceChecks" WHERE "ComplianceRuleId" IN (
            SELECT cr."Id" FROM "ComplianceRules" cr
            WHERE cr."ComplianceTemplateId" IN (SELECT dupe_id FROM _complidrop_sys_tpl_dupes)
        );

        UPDATE "Vendors" SET "ComplianceTemplateId" = d.survivor_id, "UpdatedAt" = now()
        FROM _complidrop_sys_tpl_dupes d
        WHERE "Vendors"."ComplianceTemplateId" = d.dupe_id;

        DELETE FROM "ComplianceTemplates" WHERE "Id" IN (SELECT dupe_id FROM _complidrop_sys_tpl_dupes);
        """;
}
