using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Persistence;
using Storporate.SharedKernel.Auditing;
using Storporate.SharedKernel.Persistence;

namespace Storporate.Infrastructure.Auditing;

/// <summary>
/// Default <see cref="IAuditLogWriter"/> implementation. Writes audit rows through a raw
/// <see cref="NpgsqlConnection"/> (NOT through EF Core's change tracker or its underlying
/// connection), with a Postgres advisory lock keyed by the chain's account id so concurrent
/// writers serialize through the chain tip read.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why raw NpgsqlConnection, not EF Core.</b> The audit-write path needs its own
/// transaction and its own connection — independent of whatever transaction the calling
/// request might be using. EF Core's <c>Database.GetDbConnection()</c> returns the caller's
/// connection, which is correct only when the caller is on the same transaction;
/// otherwise the audit insert would commit-or-rollback together with the caller's work,
/// and a failing audit write would silently abort a successful login. The writer
/// therefore opens its own <see cref="NpgsqlConnection"/> via
/// <see cref="ConnectionStringsOptions.ToNpgsqlConnectionString"/>, which layers the
/// configured <c>SslMode</c> onto the same connection string EF Core uses.
/// </para>
/// <para>
/// <b>Provider gate.</b> Reads the provider invariant name from
/// <see cref="ConnectionStringsOptions.WriteDbProviderName"/> rather than from a
/// co-lifecycled <c>WriteDbContext</c>; the same configuration value the host passes to
/// <c>UseNpgsql</c> is what gates the write path, so the gate stays correct under any
/// future provider swap. Under a non-Postgres provider the writer short-circuits to a
/// clean no-op — the caller can't tell the difference from a successful write.
/// </para>
/// <para>
/// <b>Failure safety.</b> The whole post-provider-gate path runs inside a try/catch that
/// logs and swallows (excluding <see cref="OperationCanceledException"/>, which the caller
/// raised and must see). An audit-write failure must never break the calling request —
/// audit is a side-channel, not part of the user's contract.
/// </para>
/// <para>
/// <b>Advisory lock.</b> <c>pg_advisory_xact_lock</c> serializes concurrent writers on the
/// same chain so the chain-tip read sees a consistent previous hash and the resulting chain
/// is gap-free. The lock key is <see cref="AdvisoryLockKey.From"/> of the ambient account id,
/// which collapses to <c>0</c> for the shared null-account chain — all pre-account events
/// (failed OTP request before any user exists) share one lock and one chain by design.
/// </para>
/// </remarks>
public sealed class AuditLogWriter : IAuditLogWriter
{
    private readonly IAccountContext _accountContext;
    private readonly ConnectionStringsOptions _connectionStrings;
    private readonly ILogger<AuditLogWriter> _logger;

    public AuditLogWriter(
        IAccountContext accountContext,
        IOptions<ConnectionStringsOptions> connectionStrings,
        ILogger<AuditLogWriter> logger)
    {
        _accountContext = accountContext;
        _connectionStrings = connectionStrings.Value;
        _logger = logger;
    }

    public async Task WriteAsync(
        string action,
        string resourceType,
        string? resourceId,
        string? metadataJson = null,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(_connectionStrings.WriteDbProviderName, "Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            await WriteUnderPostgresAsync(action, resourceType, resourceId, metadataJson, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Swallow-and-log. An audit-write failure must never break the calling request —
            // audit is a side-channel, not part of the user's contract.
            _logger.LogError(
                ex,
                "Audit log write failed for action {Action} resourceType {ResourceType} resourceId {ResourceId}.",
                action,
                resourceType,
                resourceId);
        }
    }

    private async Task WriteUnderPostgresAsync(
        string action,
        string resourceType,
        string? resourceId,
        string? metadataJson,
        CancellationToken cancellationToken)
    {
        // Own connection, not any caller's — NpgsqlConnection's async-open does not block
        // on the connection handshake; the await is real.
        await using var connection = new NpgsqlConnection(_connectionStrings.ToNpgsqlConnectionString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        // Serialize concurrent writers on the same chain. xact_lock auto-releases when this
        // transaction commits or rolls back, so the lock can't outlive the write that took it.
        var lockKey = AdvisoryLockKey.From(_accountContext.AccountId);
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@lockKey)", connection, transaction))
        {
            lockCommand.Parameters.Add(new NpgsqlParameter("@lockKey", NpgsqlDbType.Bigint) { Value = lockKey });
            await lockCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // The null-safe WHERE clause gives one shared chain for all null-AccountId rows
        // (pre-account events), exactly as the plan requires.
        string? previousHash = null;
        await using (var tipCommand = new NpgsqlCommand(
            """
            SELECT "Hash" FROM "AuditLogEntries"
            WHERE ("AccountId" = @accountId OR (@accountId IS NULL AND "AccountId" IS NULL))
            ORDER BY "SequenceNumber" DESC
            LIMIT 1
            """,
            connection,
            transaction))
        {
            tipCommand.Parameters.Add(new NpgsqlParameter("@accountId", NpgsqlDbType.Uuid)
            {
                Value = (object?)_accountContext.AccountId ?? DBNull.Value,
            });
            var result = await tipCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (result is string s)
            {
                previousHash = s;
            }
        }

        var id = Guid.NewGuid();
        var createdAt = AuditLogHash.TruncateToMicroseconds(DateTime.UtcNow);
        var hash = AuditLogHash.Compute(
            previousHash,
            id,
            _accountContext.AccountId,
            _accountContext.UserId,
            action,
            resourceType,
            resourceId,
            _accountContext.IpAddress,
            _accountContext.UserAgent,
            metadataJson,
            createdAt);

        // Insert with raw SQL, bypassing EF Core entirely. SequenceNumber is identity-always,
        // so it's omitted from the column list and Postgres allocates it. MetadataJson is
        // cast to ::jsonb in the SQL text when present so the column type is respected.
        var sql =
            $"""
            INSERT INTO "AuditLogEntries" (
                "Id",
                "AccountId",
                "ActorUserId",
                "Action",
                "ResourceType",
                "ResourceId",
                "IpAddress",
                "UserAgent",
                "MetadataJson",
                "CreatedAt",
                "Hash",
                "PreviousHash"
            )
            VALUES (
                @id,
                @accountId,
                @actorUserId,
                @action,
                @resourceType,
                @resourceId,
                @ipAddress,
                @userAgent,
                {(metadataJson is null ? "NULL" : "CAST(@metadataJson AS jsonb)")},
                @createdAt,
                @hash,
                @previousHash
            )
            """;

        await using var insertCommand = new NpgsqlCommand(sql, connection, transaction);
        insertCommand.Parameters.Add(new NpgsqlParameter("@id", NpgsqlDbType.Uuid) { Value = id });
        insertCommand.Parameters.Add(new NpgsqlParameter("@accountId", NpgsqlDbType.Uuid)
        {
            Value = (object?)_accountContext.AccountId ?? DBNull.Value,
        });
        insertCommand.Parameters.Add(new NpgsqlParameter("@actorUserId", NpgsqlDbType.Uuid)
        {
            Value = (object?)_accountContext.UserId ?? DBNull.Value,
        });
        insertCommand.Parameters.Add(new NpgsqlParameter("@action", NpgsqlDbType.Varchar) { Value = action });
        insertCommand.Parameters.Add(new NpgsqlParameter("@resourceType", NpgsqlDbType.Varchar) { Value = resourceType });
        insertCommand.Parameters.Add(new NpgsqlParameter("@resourceId", NpgsqlDbType.Varchar)
        {
            Value = (object?)resourceId ?? DBNull.Value,
        });
        insertCommand.Parameters.Add(new NpgsqlParameter("@ipAddress", NpgsqlDbType.Varchar)
        {
            Value = (object?)_accountContext.IpAddress ?? DBNull.Value,
        });
        insertCommand.Parameters.Add(new NpgsqlParameter("@userAgent", NpgsqlDbType.Varchar)
        {
            Value = (object?)_accountContext.UserAgent ?? DBNull.Value,
        });
        if (metadataJson is not null)
        {
            insertCommand.Parameters.Add(new NpgsqlParameter("@metadataJson", NpgsqlDbType.Json) { Value = metadataJson });
        }
        insertCommand.Parameters.Add(new NpgsqlParameter("@createdAt", NpgsqlDbType.TimestampTz) { Value = createdAt });
        insertCommand.Parameters.Add(new NpgsqlParameter("@hash", NpgsqlDbType.Varchar) { Value = hash });
        insertCommand.Parameters.Add(new NpgsqlParameter("@previousHash", NpgsqlDbType.Varchar)
        {
            Value = (object?)previousHash ?? DBNull.Value,
        });

        await insertCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
