using Npgsql;
using Xunit;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Aggregates;
using ZeroAlloc.EventSourcing.Sql;
using ZeroAlloc.EventSourcing.Tests;

namespace ZeroAlloc.EventSourcing.Sql.Tests;

public class SnapshotStoreProviderTests
{
    private struct TestState : IAggregateState<TestState>
    {
        public static TestState Initial => default;
    }
    private const string PostgreSqlConnectionString = "Host=localhost;Database=test;Username=test;Password=test";
    private const string SqlServerConnectionString = "Server=localhost;Database=test;User Id=sa;Password=YourPassword123!;Trust Server Certificate=true;";

    [Fact]
    public async Task CreateAsync_WithPostgreSQL_ReturnsPostgreSqlSnapshotStore()
    {
        // Arrange
        var provider = new SnapshotStoreProvider("PostgreSQL", PostgreSqlConnectionString);

        // Act
        var store = await provider.CreateAsync<TestState>(CancellationToken.None);

        // Assert
        Assert.NotNull(store);
        Assert.IsType<PostgreSqlSnapshotStore<TestState>>(store);
    }

    [Fact]
    public async Task CreateAsync_WithPostgreSQLLowerCase_ReturnsPostgreSqlSnapshotStore()
    {
        // Arrange
        var provider = new SnapshotStoreProvider("postgresql", PostgreSqlConnectionString);

        // Act
        var store = await provider.CreateAsync<TestState>(CancellationToken.None);

        // Assert
        Assert.NotNull(store);
        Assert.IsType<PostgreSqlSnapshotStore<TestState>>(store);
    }

    [Fact]
    public async Task CreateAsync_WithSqlServer_ReturnsSqlServerSnapshotStore()
    {
        // Arrange
        var provider = new SnapshotStoreProvider("SqlServer", SqlServerConnectionString);

        // Act
        var store = await provider.CreateAsync<TestState>(CancellationToken.None);

        // Assert
        Assert.NotNull(store);
        Assert.IsType<SqlServerSnapshotStore<TestState>>(store);
    }

    [Fact]
    public async Task CreateAsync_WithSqlServerLowerCase_ReturnsSqlServerSnapshotStore()
    {
        // Arrange
        var provider = new SnapshotStoreProvider("sqlserver", SqlServerConnectionString);

        // Act
        var store = await provider.CreateAsync<TestState>(CancellationToken.None);

        // Assert
        Assert.NotNull(store);
        Assert.IsType<SqlServerSnapshotStore<TestState>>(store);
    }

    [Fact]
    public void Constructor_WithNullDatabase_ThrowsArgumentNullException()
    {
        // Act & Assert
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new SnapshotStoreProvider(null!, PostgreSqlConnectionString));

        Assert.Equal("database", ex.ParamName);
    }

    [Fact]
    public void Constructor_WithNullConnectionString_ThrowsArgumentNullException()
    {
        // Act & Assert
        var ex = Assert.Throws<ArgumentNullException>(() =>
            new SnapshotStoreProvider("PostgreSQL", null!));

        Assert.Equal("connectionString", ex.ParamName);
    }

    [Fact]
    public void Constructor_WithInvalidDatabase_ThrowsArgumentException()
    {
        // Act & Assert
        var ex = Assert.Throws<ArgumentException>(() =>
            new SnapshotStoreProvider("MySQL", PostgreSqlConnectionString));

        Assert.Equal("database", ex.ParamName);
        Assert.Contains("Database must be 'PostgreSQL' or 'SqlServer'", ex.Message);
    }

    [Fact]
    public void Constructor_WithEmptyDatabase_ThrowsArgumentException()
    {
        // Act & Assert
        var ex = Assert.Throws<ArgumentException>(() =>
            new SnapshotStoreProvider("", PostgreSqlConnectionString));

        Assert.Equal("database", ex.ParamName);
        Assert.Contains("Database must be 'PostgreSQL' or 'SqlServer'", ex.Message);
    }

    [Fact]
    public async Task CreateAsync_WithCancellationToken_ThrowsOperationCanceledException()
    {
        // Arrange
        var provider = new SnapshotStoreProvider("PostgreSQL", PostgreSqlConnectionString);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            provider.CreateAsync<TestState>(cts.Token).AsTask());
    }
}
