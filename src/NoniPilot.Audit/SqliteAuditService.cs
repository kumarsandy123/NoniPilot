using Microsoft.Data.Sqlite;
using NoniPilot.Domain.Interfaces;
using NoniPilot.Domain.Models;

namespace NoniPilot.Audit;

/// <summary>
/// SQLite-backed IAuditService (section 7: "SQLite local store"). Every row is append-only -
/// there is deliberately no Update/Delete method, since section 18 requires a tamper-aware log.
/// </summary>
public sealed class SqliteAuditService : IAuditService
{
    private readonly string _connectionString;

    public SqliteAuditService(string? databasePath = null)
    {
        var path = databasePath ?? DefaultDatabasePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = $"Data Source={path}";
        EnsureSchema();
    }

    public async Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO audit_events (id, task_id, actor, action, target, outcome, timestamp, metadata_json)
            VALUES ($id, $taskId, $actor, $action, $target, $outcome, $timestamp, $metadata)
            """;
        command.Parameters.AddWithValue("$id", auditEvent.Id);
        command.Parameters.AddWithValue("$taskId", (object?)auditEvent.TaskId ?? DBNull.Value);
        command.Parameters.AddWithValue("$actor", auditEvent.Actor);
        command.Parameters.AddWithValue("$action", auditEvent.Action);
        command.Parameters.AddWithValue("$target", (object?)auditEvent.Target ?? DBNull.Value);
        command.Parameters.AddWithValue("$outcome", auditEvent.Outcome.ToString());
        command.Parameters.AddWithValue("$timestamp", auditEvent.Timestamp.ToString("O"));
        command.Parameters.AddWithValue("$metadata", auditEvent.MetadataJson);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AuditEvent>> QueryAsync(string? taskId = null, int limit = 200, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var command = connection.CreateCommand();
        command.CommandText = taskId is null
            ? "SELECT id, task_id, actor, action, target, outcome, timestamp, metadata_json FROM audit_events ORDER BY timestamp DESC LIMIT $limit"
            : "SELECT id, task_id, actor, action, target, outcome, timestamp, metadata_json FROM audit_events WHERE task_id = $taskId ORDER BY timestamp DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit);
        if (taskId is not null)
        {
            command.Parameters.AddWithValue("$taskId", taskId);
        }

        var results = new List<AuditEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new AuditEvent
            {
                Id = reader.GetString(0),
                TaskId = reader.IsDBNull(1) ? null : reader.GetString(1),
                Actor = reader.GetString(2),
                Action = reader.GetString(3),
                Target = reader.IsDBNull(4) ? null : reader.GetString(4),
                Outcome = Enum.Parse<AuditOutcome>(reader.GetString(5)),
                Timestamp = DateTimeOffset.Parse(reader.GetString(6)),
                MetadataJson = reader.GetString(7),
            });
        }

        return results;
    }

    private void EnsureSchema()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS audit_events (
                id TEXT PRIMARY KEY,
                task_id TEXT NULL,
                actor TEXT NOT NULL,
                action TEXT NOT NULL,
                target TEXT NULL,
                outcome TEXT NOT NULL,
                timestamp TEXT NOT NULL,
                metadata_json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_audit_events_task_id ON audit_events (task_id);
            """;
        command.ExecuteNonQuery();
    }

    private static string DefaultDatabasePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NoniPilot", "audit.db");
}
