using Means.Core;
using Microsoft.Data.Sqlite;

namespace Means.Infrastructure.SqliteFs;

public sealed partial class SqliteFsStore
{
    public async Task<IReadOnlyList<ErasureCodingProfile>> ListErasureCodingProfilesAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select profile_id, data_shards, parity_shards, cell_size_bytes, enabled, created_utc, updated_utc
            from erasure_coding_profiles
            order by profile_id;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var profiles = new List<ErasureCodingProfile>();
        while (await reader.ReadAsync(cancellationToken))
        {
            profiles.Add(ReadErasureCodingProfile(reader));
        }

        return profiles;
    }

    public async Task<ErasureCodingProfile?> GetErasureCodingProfileAsync(string profileId, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select profile_id, data_shards, parity_shards, cell_size_bytes, enabled, created_utc, updated_utc
            from erasure_coding_profiles
            where profile_id = $profileId;
            """;
        command.Parameters.AddWithValue("$profileId", NormalizeErasureCodingProfileId(profileId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadErasureCodingProfile(reader) : null;
    }

    public async Task<ErasureCodingProfile> SaveErasureCodingProfileAsync(ErasureCodingProfile profile, CancellationToken cancellationToken)
    {
        var normalized = ValidateErasureCodingProfile(profile);
        await EnsureInitializedAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        ErasureCodingProfile saved;
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var existing = await GetErasureCodingProfileForUpdateAsync(connection, (SqliteTransaction)transaction, normalized.ProfileId, cancellationToken);
            saved = normalized with
            {
                CreatedAt = existing?.CreatedAt ?? now,
                UpdatedAt = now
            };

            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                insert into erasure_coding_profiles(
                    profile_id,
                    data_shards,
                    parity_shards,
                    cell_size_bytes,
                    enabled,
                    created_utc,
                    updated_utc)
                values(
                    $profileId,
                    $dataShards,
                    $parityShards,
                    $cellSize,
                    $enabled,
                    $created,
                    $updated)
                on conflict(profile_id) do update set
                    data_shards = excluded.data_shards,
                    parity_shards = excluded.parity_shards,
                    cell_size_bytes = excluded.cell_size_bytes,
                    enabled = excluded.enabled,
                    updated_utc = excluded.updated_utc;
                """;
            command.Parameters.AddWithValue("$profileId", saved.ProfileId);
            command.Parameters.AddWithValue("$dataShards", saved.DataShards);
            command.Parameters.AddWithValue("$parityShards", saved.ParityShards);
            command.Parameters.AddWithValue("$cellSize", saved.CellSizeBytes);
            command.Parameters.AddWithValue("$enabled", saved.Enabled ? 1 : 0);
            command.Parameters.AddWithValue("$created", saved.CreatedAt.ToString("O"));
            command.Parameters.AddWithValue("$updated", saved.UpdatedAt.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }

        return saved;
    }

    public async Task DeleteErasureCodingProfileAsync(string profileId, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "delete from erasure_coding_profiles where profile_id = $profileId;";
        command.Parameters.AddWithValue("$profileId", NormalizeErasureCodingProfileId(profileId));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<ErasureCodingProfile?> GetErasureCodingProfileForUpdateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string profileId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            select profile_id, data_shards, parity_shards, cell_size_bytes, enabled, created_utc, updated_utc
            from erasure_coding_profiles
            where profile_id = $profileId;
            """;
        command.Parameters.AddWithValue("$profileId", profileId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadErasureCodingProfile(reader) : null;
    }

    private static ErasureCodingProfile ReadErasureCodingProfile(SqliteDataReader reader)
    {
        return new ErasureCodingProfile(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetBoolean(4),
            DateTimeOffset.Parse(reader.GetString(5)),
            DateTimeOffset.Parse(reader.GetString(6)));
    }

    private static ErasureCodingProfile ValidateErasureCodingProfile(ErasureCodingProfile profile)
    {
        var profileId = NormalizeErasureCodingProfileId(profile.ProfileId);
        if (profile.DataShards is < 2 or > 32)
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "EC data shards must be between 2 and 32.", 400);
        }

        if (profile.ParityShards is < 1 or > 16)
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "EC parity shards must be between 1 and 16.", 400);
        }

        if (profile.DataShards + profile.ParityShards > 48)
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "EC total shards must not exceed 48.", 400);
        }

        if (profile.CellSizeBytes is < 65536 or > 67108864 || !IsPowerOfTwo(profile.CellSizeBytes))
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "EC cell size must be a power of two between 64 KiB and 64 MiB.", 400);
        }

        return profile with { ProfileId = profileId };
    }

    private static string NormalizeErasureCodingProfileId(string profileId)
    {
        var normalized = profileId.Trim().ToLowerInvariant();
        if (normalized.Length is < 3 or > 64
            || normalized[0] is '-' or '.'
            || normalized[^1] is '-' or '.'
            || normalized.Any(character =>
                character is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '.')))
        {
            throw new MeansException(MeansErrorCodes.InvalidArgument, "Invalid EC profile id.", 400);
        }

        return normalized;
    }

    private static bool IsPowerOfTwo(int value)
    {
        return value > 0 && (value & (value - 1)) == 0;
    }
}
