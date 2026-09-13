using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using RaceVideoProcessor.Core.Interfaces;
using RaceVideoProcessor.Core.Models;

namespace RaceVideoProcessor.Infrastructure.Data;

public sealed class SqliteLocalStateRepository : ILocalStateRepository
{
    private readonly string _connectionString;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,

        // Settings are written on startup and on every save. A non-finite double
        // anywhere in them would otherwise throw and take the application down
        // before its window ever appears.
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    public SqliteLocalStateRepository(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;

            CREATE TABLE IF NOT EXISTS entry_local_state (
                entry_id TEXT PRIMARY KEY,
                local_video_path TEXT NULL,
                processing_status TEXT NOT NULL,
                output_path TEXT NULL,
                error_message TEXT NULL,
                last_processed_data_hash TEXT NULL,
                last_seen_data_hash TEXT NULL,
                last_seen_utc TEXT NULL,
                processed_utc TEXT NULL,
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS app_settings (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                json TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS sync_state (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                last_attempt_utc TEXT NULL,
                last_success_utc TEXT NULL,
                last_error TEXT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<EntryLocalState?> GetEntryStateAsync(string entryId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM entry_local_state WHERE entry_id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", entryId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadState(reader) : null;
    }

    public async Task<IReadOnlyDictionary<string, EntryLocalState>> GetAllEntryStatesAsync(CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, EntryLocalState>(StringComparer.OrdinalIgnoreCase);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM entry_local_state ORDER BY updated_utc DESC;";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var state = ReadState(reader);
            result[state.EntryId] = state;
        }

        return result;
    }

    public async Task UpsertEntryStateAsync(EntryLocalState state, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO entry_local_state (
                entry_id, local_video_path, processing_status, output_path, error_message,
                last_processed_data_hash, last_seen_data_hash, last_seen_utc, processed_utc, updated_utc
            ) VALUES (
                $entry_id, $local_video_path, $processing_status, $output_path, $error_message,
                $last_processed_data_hash, $last_seen_data_hash, $last_seen_utc, $processed_utc, $updated_utc
            )
            ON CONFLICT(entry_id) DO UPDATE SET
                local_video_path = excluded.local_video_path,
                processing_status = excluded.processing_status,
                output_path = excluded.output_path,
                error_message = excluded.error_message,
                last_processed_data_hash = excluded.last_processed_data_hash,
                last_seen_data_hash = excluded.last_seen_data_hash,
                last_seen_utc = excluded.last_seen_utc,
                processed_utc = excluded.processed_utc,
                updated_utc = excluded.updated_utc
            WHERE excluded.updated_utc >= entry_local_state.updated_utc;
            """;
        command.Parameters.AddWithValue("$entry_id", state.EntryId);
        command.Parameters.AddWithValue("$local_video_path", (object?)state.LocalVideoPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$processing_status", state.ProcessingStatus.ToString());
        command.Parameters.AddWithValue("$output_path", (object?)state.OutputPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$error_message", (object?)state.ErrorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$last_processed_data_hash", (object?)state.LastProcessedDataHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$last_seen_data_hash", (object?)state.LastSeenDataHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$last_seen_utc", ToDb(state.LastSeenUtc));
        command.Parameters.AddWithValue("$processed_utc", ToDb(state.ProcessedUtc));
        command.Parameters.AddWithValue("$updated_utc", state.UpdatedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AppSettings?> LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM app_settings WHERE id = 1 LIMIT 1;";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is not string json)
            return null;

        var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
        settings?.Normalize();
        return settings;
    }

    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        settings.Normalize();
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO app_settings(id, json, updated_utc)
            VALUES(1, $json, $updated_utc)
            ON CONFLICT(id) DO UPDATE SET json = excluded.json, updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$json", json);
        command.Parameters.AddWithValue("$updated_utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<SyncState> LoadSyncStateAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT last_attempt_utc, last_success_utc, last_error FROM sync_state WHERE id = 1 LIMIT 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return new SyncState(null, null, null);

        return new SyncState(
            ParseDb(reader.IsDBNull(0) ? null : reader.GetString(0)),
            ParseDb(reader.IsDBNull(1) ? null : reader.GetString(1)),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    public async Task SaveSyncStateAsync(SyncState state, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sync_state(id, last_attempt_utc, last_success_utc, last_error)
            VALUES(1, $attempt, $success, $error)
            ON CONFLICT(id) DO UPDATE SET
                last_attempt_utc = excluded.last_attempt_utc,
                last_success_utc = excluded.last_success_utc,
                last_error = excluded.last_error;
            """;
        command.Parameters.AddWithValue("$attempt", ToDb(state.LastAttemptUtc));
        command.Parameters.AddWithValue("$success", ToDb(state.LastSuccessUtc));
        command.Parameters.AddWithValue("$error", (object?)state.LastError ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static EntryLocalState ReadState(SqliteDataReader reader)
    {
        var statusText = reader.GetString(reader.GetOrdinal("processing_status"));
        if (!Enum.TryParse<LocalProcessingStatus>(statusText, true, out var status))
            status = LocalProcessingStatus.VideoNotSelected;

        return new EntryLocalState
        {
            EntryId = reader.GetString(reader.GetOrdinal("entry_id")),
            LocalVideoPath = GetNullableString(reader, "local_video_path"),
            ProcessingStatus = status,
            OutputPath = GetNullableString(reader, "output_path"),
            ErrorMessage = GetNullableString(reader, "error_message"),
            LastProcessedDataHash = GetNullableString(reader, "last_processed_data_hash"),
            LastSeenDataHash = GetNullableString(reader, "last_seen_data_hash"),
            LastSeenUtc = ParseDb(GetNullableString(reader, "last_seen_utc")),
            ProcessedUtc = ParseDb(GetNullableString(reader, "processed_utc")),
            UpdatedUtc = ParseDb(GetNullableString(reader, "updated_utc")) ?? DateTimeOffset.UtcNow
        };
    }

    private static string? GetNullableString(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static object ToDb(DateTimeOffset? value)
        => value.HasValue ? value.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) : DBNull.Value;

    private static DateTimeOffset? ParseDb(string? value)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
}
