using McpPrivateLibrary.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;

namespace McpPrivateLibrary.Data;

/// <summary>
/// Builds pooled, pgvector-enabled Npgsql connections and applies the schema on startup.
/// </summary>
public sealed class NpgsqlConnectionFactory : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlConnectionFactory(IOptions<LibraryOptions> options)
    {
        var builder = new NpgsqlDataSourceBuilder(options.Value.ConnectionString);
        builder.UseVector();
        _dataSource = builder.Build();
    }

    public NpgsqlConnection Create() => _dataSource.CreateConnection();

    public async ValueTask DisposeAsync() => await _dataSource.DisposeAsync();
}
