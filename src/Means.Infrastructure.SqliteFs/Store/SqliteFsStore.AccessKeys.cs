using Means.Core;

namespace Means.Infrastructure.SqliteFs;

public sealed partial class SqliteFsStore
{
    public async Task<AccessKeyCredential?> GetCredentialAsync(string accessKey, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "select access_key, secret_key, enabled from access_keys where access_key = $accessKey;";
        command.Parameters.AddWithValue("$accessKey", accessKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new AccessKeyCredential(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2))
            : null;
    }
}
