using System.Reflection;
using Xunit;

namespace Dps.PolicyApproval.Tests;

public sealed class NativeStopChallengeMigrationTests
{
    [Fact, Trait("Category", "Contract")]
    public void DormantMigrationDefinesAtomicExclusiveLedgerWithoutRuntimeGrant()
    {
        var assembly = typeof(PostgresPolicyApprovalMigrator).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(static name =>
            name.EndsWith("004_create_native_stop_challenge_ledger.sql", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("Native stop challenge migration resource is missing.");
        using var reader = new StreamReader(stream);
        var sql = reader.ReadToEnd();

        Assert.Contains("CREATE TABLE IF NOT EXISTS __SCHEMA__.native_stop_challenge_issues", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE IF NOT EXISTS __SCHEMA__.native_stop_challenge_consumptions", sql, StringComparison.Ordinal);
        Assert.Contains("challenge_id uuid NOT NULL UNIQUE", sql, StringComparison.Ordinal);
        Assert.Contains("submission_attempt_id uuid NOT NULL UNIQUE", sql, StringComparison.Ordinal);
        Assert.Contains("terminal_kind IN ('ACK', 'UNKNOWN')", sql, StringComparison.Ordinal);
        Assert.Contains("consume_native_stop_challenge_ack", sql, StringComparison.Ordinal);
        Assert.Contains("consume_native_stop_challenge_unknown", sql, StringComparison.Ordinal);
        Assert.Contains("DUPLICATE_NO_OP", sql, StringComparison.Ordinal);
        Assert.Contains("FOR UPDATE", sql, StringComparison.Ordinal);
        Assert.Contains(
            "ARRAY['native_stop_challenge_issues', 'native_stop_challenge_consumptions']",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "CREATE TRIGGER %I BEFORE UPDATE OR DELETE ON __SCHEMA__.%I FOR EACH ROW",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "CREATE TRIGGER %I BEFORE TRUNCATE ON __SCHEMA__.%I FOR EACH STATEMENT",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "REVOKE ALL ON __SCHEMA__.native_stop_challenge_issues FROM PUBLIC, __RUNTIME_ROLE__, __SUBMISSION_EXECUTOR_ROLE__, __SUBMISSION_RECONCILIATION_ROLE__, __SUBMISSION_RECOVERY_ROLE__;",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "REVOKE ALL ON __SCHEMA__.native_stop_challenge_consumptions FROM PUBLIC, __RUNTIME_ROLE__, __SUBMISSION_EXECUTOR_ROLE__, __SUBMISSION_RECONCILIATION_ROLE__, __SUBMISSION_RECOVERY_ROLE__;",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "REVOKE ALL ON FUNCTION __SCHEMA__.issue_native_stop_challenge(uuid, uuid, bytea, text, text, text, text, timestamptz) FROM PUBLIC, __RUNTIME_ROLE__, __SUBMISSION_EXECUTOR_ROLE__, __SUBMISSION_RECONCILIATION_ROLE__, __SUBMISSION_RECOVERY_ROLE__;",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "REVOKE ALL ON FUNCTION __SCHEMA__.consume_native_stop_challenge_ack(uuid, uuid, text, jsonb, text, jsonb, text) FROM PUBLIC, __RUNTIME_ROLE__, __SUBMISSION_EXECUTOR_ROLE__, __SUBMISSION_RECONCILIATION_ROLE__, __SUBMISSION_RECOVERY_ROLE__;",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "REVOKE ALL ON FUNCTION __SCHEMA__.consume_native_stop_challenge_unknown(uuid, uuid, uuid, text, bytea, text, text, jsonb, text) FROM PUBLIC, __RUNTIME_ROLE__, __SUBMISSION_EXECUTOR_ROLE__, __SUBMISSION_RECONCILIATION_ROLE__, __SUBMISSION_RECOVERY_ROLE__;",
            sql,
            StringComparison.Ordinal);
        Assert.DoesNotContain("GRANT EXECUTE ON FUNCTION __SCHEMA__.issue_native_stop_challenge", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("GRANT EXECUTE ON FUNCTION __SCHEMA__.consume_native_stop_challenge_ack", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("GRANT EXECUTE ON FUNCTION __SCHEMA__.consume_native_stop_challenge_unknown", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("GRANT SELECT ON __SCHEMA__.native_stop_challenge", sql, StringComparison.Ordinal);
    }

    [Fact, Trait("Category", "Unit")]
    public void MigrationUsesScopeApprovalCommandAttemptFencingOrder()
    {
        var assembly = typeof(PostgresPolicyApprovalMigrator).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(static name =>
            name.EndsWith("004_create_native_stop_challenge_ledger.sql", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("Native stop challenge migration resource is missing.");
        using var reader = new StreamReader(stream);
        var sql = reader.ReadToEnd();
        foreach (var function in new[]
        {
            "issue_native_stop_challenge",
            "consume_native_stop_challenge_ack",
            "consume_native_stop_challenge_unknown"
        })
        {
            var start = sql.IndexOf("CREATE OR REPLACE FUNCTION __SCHEMA__." + function, StringComparison.Ordinal);
            Assert.True(start >= 0, $"Missing {function}.");
            var end = sql.IndexOf("$function$;", start, StringComparison.Ordinal);
            Assert.True(end > start, $"Unterminated {function}.");
            var body = sql[start..end];
            const string advisoryLockPrefix = "pg_advisory_xact_lock(hashtextextended('";
            var scope = body.IndexOf(advisoryLockPrefix + "policy-runtime:", StringComparison.Ordinal);
            var approval = body.IndexOf(advisoryLockPrefix + "approval:", StringComparison.Ordinal);
            var command = body.IndexOf(advisoryLockPrefix + "submission-command:", StringComparison.Ordinal);
            var attempt = body.IndexOf(advisoryLockPrefix + "submission-attempt:", StringComparison.Ordinal);
            Assert.True(scope >= 0 && scope < approval && approval < command && command < attempt,
                $"{function} does not use the canonical fence order.");
        }
    }
}
