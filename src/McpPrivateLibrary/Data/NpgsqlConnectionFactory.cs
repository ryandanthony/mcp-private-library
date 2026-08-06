using McpPrivateLibrary.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;

namespace McpPrivateLibrary.Data;

/// <summary>
/// Builds pooled, pgvector-enabled Npgsql connections.
///
/// Important ordering: pgvector's <c>UseVector()</c> resolves the <c>vector</c> type when the
/// data source is built / first connects. On a brand-new database the extension does not exist
/// yet, so we first ensure <c>CREATE EXTENSION vector</c> over a plain bootstrap connection, and
/// only then build the vector-enabled data source. Otherwise writing a <see cref="Pgvector.Vector"/>
/// parameter fails with "no NpgsqlDbType or DataTypeName".
/// </summary>
public sealed class NpgsqlConnectionFactory : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlConnectionFactory(IOptions<LibraryOptions> options)
    {
        var connectionString = options.Value.ConnectionString;

        EnsureVectorExtension(connectionString);

        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.UseVector();
        _dataSource = builder.Build();
    }

    /// <summary>
    /// Creates the pgvector extension using a throwaway connection that does NOT map the vector
    /// type. This guarantees the type exists before the main data source resolves its type catalog.
    /// </summary>
    private static void EnsureVectorExtension(string connectionString)
    {
        using var conn = new NpgsqlConnection(connectionString);
        conn.Open();
        using var cmd = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS vector;", conn);
        cmd.ExecuteNonQuery();
    }

    public NpgsqlConnection Create() => _dataSource.CreateConnection();

    public async ValueTask DisposeAsync() => await _dataSource.DisposeAsync();
}
