namespace ZeroAlloc.EventSourcing.Testing;

/// <summary>
/// An empty database created for one test on a shared container. Disposing it drops the
/// database, so the container does not collect one database per test for the whole run.
/// </summary>
public sealed class TestDatabase : IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly Func<Task> _drop;

    internal TestDatabase(string connectionString, Func<Task> drop)
    {
        _connectionString = connectionString;
        _drop = drop;
    }

    /// <summary>A connection string that targets this database.</summary>
    public string GetConnectionString() => _connectionString;

    /// <inheritdoc/>
    public async ValueTask DisposeAsync() => await _drop();
}
