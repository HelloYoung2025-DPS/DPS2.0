using System.Security.Cryptography;
using Dps.PolicyApproval.Contracts;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Dps.PolicyApproval.Tests;

public sealed partial class PostgresPolicyApprovalIntegrationTests
{
    [Fact, Trait("Category", "Integration")]
    public async Task PublicNativeSubmissionIsWaitingExternalBeforePending()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await PolicyApprovalTestDatabase.CreateAsync(cancellationToken);
        using var evaluationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var revocationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var fenceSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var executorSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var reconciliationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var recoverySigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var stateSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var topology = SubmissionTopology(
            evaluationSigner, revocationSigner, fenceSigner, executorSigner,
            reconciliationSigner, recoverySigner, stateSigner);
        var (proposal, snapshot) = await IssueApprovedAsync(
            database, evaluationSigner, revocationSigner, topology,
            "native-waiting-external",
            cancellationToken);
        var request = FenceRequest(snapshot);
        var intent = SignSubmissionIntent(executorSigner, SubmissionIntent(snapshot, proposal, request));
        using var client = CreateSubmissionClient(
            database, topology, fenceSigner, executorSigner,
            reconciliationSigner, recoverySigner, stateSigner);
        await using var lease = await client.AcquireAsync(
            request,
            SignFenceAuthorization(fenceSigner, request),
            intent,
            cancellationToken);

        var exception = await Assert.ThrowsAsync<PolicyApprovalWaitingExternalException>(
            () => lease.SubmitNativeOnceAsync(cancellationToken));

        Assert.StartsWith("WAITING_EXTERNAL:", exception.Message, StringComparison.Ordinal);
        await using var connection = new NpgsqlConnection(database.AdminConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT count(*) FROM {database.SchemaName}.approval_submission_attempts WHERE submission_attempt_id = @attempt_id",
            connection);
        command.Parameters.AddWithValue("attempt_id", intent.SubmissionAttemptId);
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidDataException("Submission count query returned null.")));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task DormantNativeStopLedgerHasRealPostgresCatalogAclAndMutationGuards()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await PolicyApprovalTestDatabase.CreateAsync(cancellationToken);
        await AssertDormantNativeStopCatalogAndAclAsync(database, cancellationToken);
        await AssertEveryRuntimeRoleIsDeniedAsync(database, cancellationToken);

        using var evaluationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var revocationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var fenceSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var executorSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var reconciliationSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var recoverySigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var stateSigner = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var topology = SubmissionTopology(
            evaluationSigner, revocationSigner, fenceSigner, executorSigner,
            reconciliationSigner, recoverySigner, stateSigner);
        var (proposal, snapshot) = await IssueApprovedAsync(
            database, evaluationSigner, revocationSigner, topology,
            "native-ledger-append-only",
            cancellationToken);
        var request = FenceRequest(snapshot);
        var intent = SignSubmissionIntent(executorSigner, SubmissionIntent(snapshot, proposal, request));
        using var client = CreateSubmissionClient(
            database, topology, fenceSigner, executorSigner,
            reconciliationSigner, recoverySigner, stateSigner);
        var lease = await client.AcquireAsync(
            request,
            SignFenceAuthorization(fenceSigner, request),
            intent,
            cancellationToken);
        var pendingCreated = false;
        try
        {
            var begin = await lease.BeginSubmissionAsync(intent, cancellationToken);
            Assert.True(begin.MaySubmit);
            pendingCreated = true;
            await SeedDormantNativeStopRowsAsync(database, intent, begin.PendingReceipt, cancellationToken);
            await AssertOwnerMutationTriggersRejectAsync(database, cancellationToken);
        }
        finally
        {
            if (pendingCreated)
                await Assert.ThrowsAsync<InvalidOperationException>(() => lease.DisposeAsync().AsTask());
            else
                await lease.DisposeAsync();
        }
    }

    private static async Task AssertDormantNativeStopCatalogAndAclAsync(
        PolicyApprovalTestDatabase database,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(database.AdminConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var triggers = new NpgsqlCommand(
            """
            SELECT table_object.relname, trigger.tgname, trigger.tgtype
              FROM pg_trigger AS trigger
              JOIN pg_class AS table_object ON table_object.oid = trigger.tgrelid
              JOIN pg_namespace AS namespace ON namespace.oid = table_object.relnamespace
             WHERE namespace.nspname = @schema_name
               AND table_object.relname IN ('native_stop_challenge_issues', 'native_stop_challenge_consumptions')
               AND NOT trigger.tgisinternal
             ORDER BY table_object.relname, trigger.tgname
            """,
            connection) { CommandTimeout = 5 })
        {
            triggers.Parameters.AddWithValue("schema_name", database.SchemaName);
            var actual = new List<string>();
            await using var reader = await triggers.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                actual.Add($"{reader.GetString(0)}:{reader.GetString(1)}:{reader.GetInt16(2)}");
            Assert.Equal(
                new[]
                {
                    "native_stop_challenge_consumptions:native_stop_challenge_consumptions_append_only:27",
                    "native_stop_challenge_consumptions:native_stop_challenge_consumptions_no_truncate:34",
                    "native_stop_challenge_issues:native_stop_challenge_issues_append_only:27",
                    "native_stop_challenge_issues:native_stop_challenge_issues_no_truncate:34"
                },
                actual);
        }

        await using var functions = new NpgsqlCommand(
            """
            SELECT function.proname,
                   has_function_privilege(@runtime_role::name, function.oid, 'EXECUTE'),
                   has_function_privilege(@executor_role::name, function.oid, 'EXECUTE'),
                   has_function_privilege(@reconciliation_role::name, function.oid, 'EXECUTE'),
                   has_function_privilege(@recovery_role::name, function.oid, 'EXECUTE'),
                   NOT EXISTS (
                       SELECT 1
                         FROM aclexplode(COALESCE(function.proacl, acldefault('f', function.proowner))) AS acl
                        WHERE acl.grantee = 0 AND acl.privilege_type = 'EXECUTE') AS public_denied
              FROM pg_proc AS function
              JOIN pg_namespace AS namespace ON namespace.oid = function.pronamespace
             WHERE namespace.nspname = @schema_name
               AND function.proname IN (
                   'issue_native_stop_challenge',
                   'consume_native_stop_challenge_ack',
                   'consume_native_stop_challenge_unknown')
             ORDER BY function.proname
            """,
            connection) { CommandTimeout = 5 };
        functions.Parameters.AddWithValue("schema_name", database.SchemaName);
        functions.Parameters.AddWithValue("runtime_role", database.RuntimeRoleName);
        functions.Parameters.AddWithValue("executor_role", database.SubmissionExecutorRoleName);
        functions.Parameters.AddWithValue("reconciliation_role", database.ReconciliationRoleName);
        functions.Parameters.AddWithValue("recovery_role", database.RecoveryRoleName);
        var functionCount = 0;
        await using var functionReader = await functions.ExecuteReaderAsync(cancellationToken);
        while (await functionReader.ReadAsync(cancellationToken))
        {
            functionCount++;
            Assert.False(functionReader.GetBoolean(1), functionReader.GetString(0));
            Assert.False(functionReader.GetBoolean(2), functionReader.GetString(0));
            Assert.False(functionReader.GetBoolean(3), functionReader.GetString(0));
            Assert.False(functionReader.GetBoolean(4), functionReader.GetString(0));
            Assert.True(functionReader.GetBoolean(5), functionReader.GetString(0));
        }
        Assert.Equal(3, functionCount);
    }

    private static async Task AssertEveryRuntimeRoleIsDeniedAsync(
        PolicyApprovalTestDatabase database,
        CancellationToken cancellationToken)
    {
        var deniedSql = new[]
        {
            $"SELECT count(*) FROM {database.SchemaName}.native_stop_challenge_issues",
            $"SELECT count(*) FROM {database.SchemaName}.native_stop_challenge_consumptions",
            $"SELECT {database.SchemaName}.issue_native_stop_challenge(NULL::uuid, NULL::uuid, NULL::bytea, NULL::text, NULL::text, NULL::text, NULL::text, NULL::timestamptz)",
            $"SELECT {database.SchemaName}.consume_native_stop_challenge_ack(NULL::uuid, NULL::uuid, NULL::text, NULL::jsonb, NULL::text, NULL::jsonb, NULL::text)",
            $"SELECT {database.SchemaName}.consume_native_stop_challenge_unknown(NULL::uuid, NULL::uuid, NULL::uuid, NULL::text, NULL::bytea, NULL::text, NULL::text, NULL::jsonb, NULL::text)"
        };
        foreach (var roleConnectionString in new[]
                 {
                     database.Options.RuntimeConnectionString,
                     database.SubmissionExecutorOptions.ExecutorConnectionString,
                     database.SubmissionReconciliationOptions.ReconciliationConnectionString,
                     database.SubmissionRecoveryOptions.RecoveryConnectionString
                 })
        {
            await using var connection = new NpgsqlConnection(roleConnectionString);
            await connection.OpenAsync(cancellationToken);
            foreach (var sql in deniedSql)
                await PolicyApprovalTestDatabase.AssertSqlStateAsync(
                    connection,
                    sql,
                    PostgresErrorCodes.InsufficientPrivilege,
                    cancellationToken);
        }
    }

    private static async Task SeedDormantNativeStopRowsAsync(
        PolicyApprovalTestDatabase database,
        ApprovalSubmissionIntentV1 intent,
        ApprovalSubmissionStateV1 pending,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(database.AdminConnectionString);
        await connection.OpenAsync(cancellationToken);
        var challengeId = Guid.NewGuid();
        await using (var issue = new NpgsqlCommand(
            $"""
            INSERT INTO {database.SchemaName}.native_stop_challenge_issues
            (challenge_id, submission_attempt_id, command_id, lease_id, attempt,
             native_request_binding_sha256, pending_state_sha256, submitted_request_sha256,
             challenge_nonce_sha256, native_abort_challenge_sha256, challenge_wire_sha256,
             challenge_wire, challenge_json, valid_until, issued_at)
            VALUES
            (@challenge_id, @submission_attempt_id, @command_id, @lease_id, @attempt,
             @native_request_binding_sha256, @pending_state_sha256, @submitted_request_sha256,
             @challenge_nonce_sha256, @native_abort_challenge_sha256, @challenge_wire_sha256,
             @challenge_wire, @challenge_json, clock_timestamp() + interval '5 minutes', clock_timestamp())
            """,
            connection) { CommandTimeout = 5 })
        {
            issue.Parameters.AddWithValue("challenge_id", challengeId);
            issue.Parameters.AddWithValue("submission_attempt_id", intent.SubmissionAttemptId);
            issue.Parameters.AddWithValue("command_id", intent.CommandId);
            issue.Parameters.AddWithValue("lease_id", intent.LeaseId);
            issue.Parameters.AddWithValue("attempt", intent.Attempt);
            issue.Parameters.AddWithValue("native_request_binding_sha256", intent.NativeRequestBindingSha256);
            issue.Parameters.AddWithValue("pending_state_sha256", pending.StateSha256);
            issue.Parameters.AddWithValue("submitted_request_sha256", Sha256Hex("native-stop-submitted-request"));
            issue.Parameters.AddWithValue("challenge_nonce_sha256", Sha256Hex("native-stop-challenge-nonce"));
            issue.Parameters.AddWithValue("native_abort_challenge_sha256", Sha256Hex("native-stop-abort-challenge"));
            issue.Parameters.AddWithValue("challenge_wire_sha256", Sha256Hex("native-stop-challenge-wire"));
            issue.Parameters.AddWithValue("challenge_wire", NpgsqlDbType.Bytea, new byte[] { (byte)'{', (byte)'}' });
            issue.Parameters.AddWithValue("challenge_json", NpgsqlDbType.Jsonb, "{}");
            await issue.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var consumption = new NpgsqlCommand(
            $"""
            INSERT INTO {database.SchemaName}.native_stop_challenge_consumptions
            (consumption_id, challenge_id, submission_attempt_id, terminal_kind,
             terminal_evidence_sha256, consumed_at)
            VALUES
            (@consumption_id, @challenge_id, @submission_attempt_id, 'ACK',
             @terminal_evidence_sha256, clock_timestamp())
            """,
            connection) { CommandTimeout = 5 };
        consumption.Parameters.AddWithValue("consumption_id", Guid.NewGuid());
        consumption.Parameters.AddWithValue("challenge_id", challengeId);
        consumption.Parameters.AddWithValue("submission_attempt_id", intent.SubmissionAttemptId);
        consumption.Parameters.AddWithValue("terminal_evidence_sha256", Sha256Hex("native-stop-terminal-evidence"));
        await consumption.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AssertOwnerMutationTriggersRejectAsync(
        PolicyApprovalTestDatabase database,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(database.AdminConnectionString);
        await connection.OpenAsync(cancellationToken);
        foreach (var table in new[]
                 {
                     "native_stop_challenge_issues",
                     "native_stop_challenge_consumptions"
                 })
        {
            await PolicyApprovalTestDatabase.AssertSqlStateAsync(
                connection,
                $"UPDATE {database.SchemaName}.{table} SET challenge_id = challenge_id",
                PostgresErrorCodes.RaiseException,
                cancellationToken);
            await PolicyApprovalTestDatabase.AssertSqlStateAsync(
                connection,
                $"DELETE FROM {database.SchemaName}.{table}",
                PostgresErrorCodes.RaiseException,
                cancellationToken);
        }
        await PolicyApprovalTestDatabase.AssertSqlStateAsync(
            connection,
            $"TRUNCATE TABLE {database.SchemaName}.native_stop_challenge_consumptions",
            PostgresErrorCodes.RaiseException,
            cancellationToken);
        await PolicyApprovalTestDatabase.AssertSqlStateAsync(
            connection,
            $"TRUNCATE TABLE {database.SchemaName}.native_stop_challenge_issues CASCADE",
            PostgresErrorCodes.RaiseException,
            cancellationToken);
    }
}
