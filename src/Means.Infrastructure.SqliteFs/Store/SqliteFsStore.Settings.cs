using Means.Core;

namespace Means.Infrastructure.SqliteFs;

public sealed partial class SqliteFsStore
{
    private const string MaxUploadSizeBytesSettingKey = "max_upload_size_bytes";

    public async Task<SystemSettings?> GetSystemSettingsAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "select value from system_settings where key = $key;";
        command.Parameters.AddWithValue("$key", MaxUploadSizeBytesSettingKey);

        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null || value == DBNull.Value)
        {
            return null;
        }

        return long.TryParse(Convert.ToString(value), out var maxUploadSizeBytes)
            ? new SystemSettings(maxUploadSizeBytes)
            : null;
    }

    public async Task SaveSystemSettingsAsync(SystemSettings settings, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            insert into system_settings(key, value, updated_utc)
            values($key, $value, $updated)
            on conflict(key) do update set
                value = excluded.value,
                updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$key", MaxUploadSizeBytesSettingKey);
        command.Parameters.AddWithValue("$value", settings.MaxUploadSizeBytes.ToString());
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
