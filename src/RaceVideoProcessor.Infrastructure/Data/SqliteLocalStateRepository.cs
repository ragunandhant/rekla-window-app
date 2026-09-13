using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using RaceVideoProcessor.Core.Interfaces;
using RaceVideoProcessor.Core.Models;

namespace RaceVideoProcessor.Infrastructure.Data;

/// <summary>
/// The application's single SQLite database.
///
/// Schema history, all additive so an existing database upgrades in place:
///   v1  entry_local_state (unscoped), app_settings, sync_state
///   v2  races, race_entry_state — entry state keyed by (race_id, race_type, entry_id).
///
/// entry_local_state predates races and cannot be attributed to one, so it is left
/// untouched and no longer read. Every entry query is scoped to a race, and a
/// race's rows are removed together with the race in one transaction.
/// </summary>
public sealed class SqliteLocalStateRepository : ILocalStateRepository
{
    private const int SchemaVersion = 2;

    private readonly string _connectionString;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,

        // Settings are written on startup and on every save. A non-finite double
        // anywhere in them would otherwise throw and take the application down
        // before its window ever appears.
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() }
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
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;

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

            CREATE TABLE IF NOT EXISTS races (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                race_id TEXT NOT NULL UNIQUE COLLATE NOCASE,
                race_name TEXT NOT NULL,
                race_date TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS race_entry_state (
                race_id TEXT NOT NULL COLLATE NOCASE
                    REFERENCES races(race_id) ON DELETE CASCADE,
                race_type TEXT NOT NULL,
                entry_id TEXT NOT NULL COLLATE NOCASE,
                cart_no TEXT NULL,
                player_id TEXT NULL,
                marker TEXT NULL,
                entry_date_utc TEXT NULL,
                remote_video_link TEXT NULL,
                original_file TEXT NULL,
                processed_file TEXT NULL,
                processing_status TEXT NOT NULL,
                upload_status TEXT NOT NULL,
                uploaded_video_link TEXT NULL,
                assignment_status TEXT NOT NULL,
                auth_failure INTEGER NOT NULL DEFAULT 0,
                error_message TEXT NULL,
                last_processed_data_hash TEXT NULL,
                last_seen_data_hash TEXT NULL,
                last_seen_utc TEXT NULL,
                processed_utc TEXT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                PRIMARY KEY (race_id, race_type, entry_id)
            );

            CREATE INDEX IF NOT EXISTS ix_race_entry_state_status
                ON race_entry_state (processing_status, upload_status, assignment_status);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        var version = connection.CreateCommand();
        version.CommandText = $"PRAGMA user_version = {SchemaVersion};";
        await version.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // ---- Races ---------------------------------------------------------------

    public async Task<IReadOnlyList<Race>> GetRacesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM races ORDER BY race_date DESC, race_name COLLATE NOCASE;";

        var races = new List<Race>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            races.Add(ReadRace(reader));
        return races;
    }

    public async Task<Race?> GetRaceAsync(string raceId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM races WHERE race_id = $race_id LIMIT 1;";
        command.Parameters.AddWithValue("$race_id", raceId.Trim());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadRace(reader) : null;
    }

    public async Task<Race> AddRaceAsync(Race race, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO races (race_id, race_name, race_date, created_utc, updated_utc)
            VALUES ($race_id, $race_name, $race_date, $created_utc, $updated_utc)
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$race_id", race.RaceId.Trim());
        command.Parameters.AddWithValue("$race_name", race.RaceName.Trim());
        command.Parameters.AddWithValue("$race_date", race.RaceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$created_utc", ToDb(now));
        command.Parameters.AddWithValue("$updated_utc", ToDb(now));

        try
        {
            var id = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
            return race with { Id = id, RaceId = race.RaceId.Trim(), RaceName = race.RaceName.Trim(), CreatedUtc = now, UpdatedUtc = now };
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // SQLITE_CONSTRAINT: race_id is UNIQUE
        {
            throw new DuplicateRaceIdException(race.RaceId.Trim());
        }
    }

    public async Task<bool> DeleteRaceAsync(string raceId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Child rows are deleted explicitly rather than relying on the cascade
            // alone, so the outcome does not depend on the foreign_keys pragma.
            var states = connection.CreateCommand();
            states.Transaction = transaction;
            states.CommandText = "DELETE FROM race_entry_state WHERE race_id = $race_id;";
            states.Parameters.AddWithValue("$race_id", raceId.Trim());
            await states.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            var race = connection.CreateCommand();
            race.Transaction = transaction;
            race.CommandText = "DELETE FROM races WHERE race_id = $race_id;";
            race.Parameters.AddWithValue("$race_id", raceId.Trim());
            var removed = await race.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return removed > 0;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    // ---- Entry state ---------------------------------------------------------

    public async Task<EntryLocalState?> GetEntryStateAsync(RaceScope scope, string entryId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM race_entry_state
            WHERE race_id = $race_id AND race_type = $race_type AND entry_id = $entry_id
            LIMIT 1;
            """;
        AddScope(command, scope);
        command.Parameters.AddWithValue("$entry_id", entryId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadState(reader) : null;
    }

    public async Task<IReadOnlyDictionary<string, EntryLocalState>> GetEntryStatesAsync(RaceScope scope, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, EntryLocalState>(StringComparer.OrdinalIgnoreCase);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM race_entry_state WHERE race_id = $race_id AND race_type = $race_type;";
        AddScope(command, scope);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var state = ReadState(reader);
            result[state.EntryId] = state;
        }

        return result;
    }

    public async Task<IReadOnlyList<EntryLocalState>> GetInFlightEntryStatesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM race_entry_state
            WHERE processing_status = 'Processing' OR upload_status = 'Uploading' OR assignment_status = 'Assigning';
            """;

        var states = new List<EntryLocalState>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            states.Add(ReadState(reader));
        return states;
    }

    public async Task<int> CountEntryStatesAsync(string raceId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM race_entry_state WHERE race_id = $race_id;";
        command.Parameters.AddWithValue("$race_id", raceId.Trim());
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    public async Task UpsertEntryStateAsync(EntryLocalState state, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(state.RaceId))
            throw new InvalidOperationException("Entry state must belong to a race.");

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();

        // The race must still exist: a job finishing after its race was deleted
        // must not bring back rows for a race that is gone.
        command.CommandText = """
            INSERT INTO race_entry_state (
                race_id, race_type, entry_id, cart_no, player_id, marker, entry_date_utc, remote_video_link,
                original_file, processed_file, processing_status, upload_status, uploaded_video_link,
                assignment_status, auth_failure, error_message, last_processed_data_hash, last_seen_data_hash,
                last_seen_utc, processed_utc, created_utc, updated_utc
            )
            SELECT
                $race_id, $race_type, $entry_id, $cart_no, $player_id, $marker, $entry_date_utc, $remote_video_link,
                $original_file, $processed_file, $processing_status, $upload_status, $uploaded_video_link,
                $assignment_status, $auth_failure, $error_message, $last_processed_data_hash, $last_seen_data_hash,
                $last_seen_utc, $processed_utc, $created_utc, $updated_utc
            WHERE EXISTS (SELECT 1 FROM races WHERE race_id = $race_id)
            ON CONFLICT(race_id, race_type, entry_id) DO UPDATE SET
                cart_no = excluded.cart_no,
                player_id = excluded.player_id,
                marker = excluded.marker,
                entry_date_utc = excluded.entry_date_utc,
                remote_video_link = excluded.remote_video_link,
                original_file = excluded.original_file,
                processed_file = excluded.processed_file,
                processing_status = excluded.processing_status,
                upload_status = excluded.upload_status,
                uploaded_video_link = excluded.uploaded_video_link,
                assignment_status = excluded.assignment_status,
                auth_failure = excluded.auth_failure,
                error_message = excluded.error_message,
                last_processed_data_hash = excluded.last_processed_data_hash,
                last_seen_data_hash = excluded.last_seen_data_hash,
                last_seen_utc = excluded.last_seen_utc,
                processed_utc = excluded.processed_utc,
                updated_utc = excluded.updated_utc
            WHERE excluded.updated_utc >= race_entry_state.updated_utc;
            """;
        AddScope(command, state.Scope);
        command.Parameters.AddWithValue("$entry_id", state.EntryId);
        command.Parameters.AddWithValue("$cart_no", Db(state.CardNumber));
        command.Parameters.AddWithValue("$player_id", Db(state.PlayerId));
        command.Parameters.AddWithValue("$marker", Db(state.Marker));
        command.Parameters.AddWithValue("$entry_date_utc", ToDb(state.EntryDateUtc));
        command.Parameters.AddWithValue("$remote_video_link", Db(state.RemoteVideoLink));
        command.Parameters.AddWithValue("$original_file", Db(state.LocalVideoPath));
        command.Parameters.AddWithValue("$processed_file", Db(state.OutputPath));
        command.Parameters.AddWithValue("$processing_status", state.ProcessingStatus.ToString());
        command.Parameters.AddWithValue("$upload_status", state.UploadStatus.ToString());
        command.Parameters.AddWithValue("$uploaded_video_link", Db(state.UploadedVideoLink));
        command.Parameters.AddWithValue("$assignment_status", state.AssignmentStatus.ToString());
        command.Parameters.AddWithValue("$auth_failure", state.LastFailureWasAuthentication ? 1 : 0);
        command.Parameters.AddWithValue("$error_message", Db(state.ErrorMessage));
        command.Parameters.AddWithValue("$last_processed_data_hash", Db(state.LastProcessedDataHash));
        command.Parameters.AddWithValue("$last_seen_data_hash", Db(state.LastSeenDataHash));
        command.Parameters.AddWithValue("$last_seen_utc", ToDb(state.LastSeenUtc));
        command.Parameters.AddWithValue("$processed_utc", ToDb(state.ProcessedUtc));
        command.Parameters.AddWithValue("$created_utc", ToDb(state.CreatedUtc));
        command.Parameters.AddWithValue("$updated_utc", ToDb(state.UpdatedUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // ---- Settings and sync ---------------------------------------------------

    public async Task<AppSettings?> LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM app_settings WHERE id = 1 LIMIT 1;";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is not string json)
            return null;

        var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
        if (settings is null)
            return null;

        settings.LoginPassword = SecretProtector.Unprotect(settings.LoginPasswordProtected);
        settings.Normalize();
        return settings;
    }

    public async Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        settings.Normalize();
        settings.LoginPasswordProtected = SecretProtector.Protect(settings.LoginPassword);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO app_settings(id, json, updated_utc)
            VALUES(1, $json, $updated_utc)
            ON CONFLICT(id) DO UPDATE SET json = excluded.json, updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$json", json);
        command.Parameters.AddWithValue("$updated_utc", ToDb(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<SyncState> LoadSyncStateAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
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
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
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
        command.Parameters.AddWithValue("$error", Db(state.LastError));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // ---- Helpers -------------------------------------------------------------

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON;";
        await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static void AddScope(SqliteCommand command, RaceScope scope)
    {
        command.Parameters.AddWithValue("$race_id", scope.RaceId.Trim());
        command.Parameters.AddWithValue("$race_type", scope.Category.ToString());
    }

    private static Race ReadRace(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(reader.GetOrdinal("id")),
        RaceId = reader.GetString(reader.GetOrdinal("race_id")),
        RaceName = reader.GetString(reader.GetOrdinal("race_name")),
        RaceDate = DateOnly.ParseExact(reader.GetString(reader.GetOrdinal("race_date")), "yyyy-MM-dd", CultureInfo.InvariantCulture),
        CreatedUtc = ParseDb(GetNullableString(reader, "created_utc")) ?? DateTimeOffset.UtcNow,
        UpdatedUtc = ParseDb(GetNullableString(reader, "updated_utc")) ?? DateTimeOffset.UtcNow
    };

    private static EntryLocalState ReadState(SqliteDataReader reader) => new()
    {
        RaceId = reader.GetString(reader.GetOrdinal("race_id")),
        Category = ParseEnum(GetNullableString(reader, "race_type"), RaceCategory.Meter200),
        EntryId = reader.GetString(reader.GetOrdinal("entry_id")),
        CardNumber = GetNullableString(reader, "cart_no"),
        PlayerId = GetNullableString(reader, "player_id"),
        Marker = GetNullableString(reader, "marker"),
        EntryDateUtc = ParseDb(GetNullableString(reader, "entry_date_utc")),
        RemoteVideoLink = GetNullableString(reader, "remote_video_link"),
        LocalVideoPath = GetNullableString(reader, "original_file"),
        OutputPath = GetNullableString(reader, "processed_file"),
        ProcessingStatus = ParseEnum(GetNullableString(reader, "processing_status"), LocalProcessingStatus.VideoNotSelected),
        UploadStatus = ParseEnum(GetNullableString(reader, "upload_status"), UploadStatus.NotStarted),
        UploadedVideoLink = GetNullableString(reader, "uploaded_video_link"),
        AssignmentStatus = ParseEnum(GetNullableString(reader, "assignment_status"), AssignmentStatus.NotStarted),
        LastFailureWasAuthentication = reader.GetInt64(reader.GetOrdinal("auth_failure")) != 0,
        ErrorMessage = GetNullableString(reader, "error_message"),
        LastProcessedDataHash = GetNullableString(reader, "last_processed_data_hash"),
        LastSeenDataHash = GetNullableString(reader, "last_seen_data_hash"),
        LastSeenUtc = ParseDb(GetNullableString(reader, "last_seen_utc")),
        ProcessedUtc = ParseDb(GetNullableString(reader, "processed_utc")),
        CreatedUtc = ParseDb(GetNullableString(reader, "created_utc")) ?? DateTimeOffset.UtcNow,
        UpdatedUtc = ParseDb(GetNullableString(reader, "updated_utc")) ?? DateTimeOffset.UtcNow
    };

    private static T ParseEnum<T>(string? value, T fallback) where T : struct, Enum
        => Enum.TryParse<T>(value, true, out var parsed) ? parsed : fallback;

    private static string? GetNullableString(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static object Db(string? value) => (object?)value ?? DBNull.Value;

    private static object ToDb(DateTimeOffset? value)
        => value.HasValue ? value.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) : DBNull.Value;

    private static DateTimeOffset? ParseDb(string? value)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
}
