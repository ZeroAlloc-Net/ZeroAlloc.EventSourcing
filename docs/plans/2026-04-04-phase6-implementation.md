# Phase 6: SQL Snapshot Stores — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task.

**Goal:** Implement production-grade snapshot persistence for PostgreSQL and SQL Server, enabling durable snapshot storage without modifying Phase 5.1 decorator logic.

**Architecture:** Two database-specific implementations (PostgreSqlSnapshotStore, SqlServerSnapshotStore) both conforming to ISnapshotStore<TState> interface. Each uses optimized SQL patterns for its database (BYTEA/ON CONFLICT for PostgreSQL, VARBINARY/MERGE for SQL Server). Shared schema, separate implementations.

**Tech Stack:** Npgsql (PostgreSQL), SQL Server ADO.NET, Testcontainers for integration tests, IEventSerializer from Phase 2 for snapshot payload serialization.

---

## Task 1: Create Shared Schema Migration & ISnapshotStore Interface Validation

**Files:**
- Create: `src/ZeroAlloc.EventSourcing.Sql/SnapshotSchema.cs`
- Modify: `src/ZeroAlloc.EventSourcing/ISnapshotStore.cs` (verify interface exists from Phase 5)
- Create: `tests/ZeroAlloc.EventSourcing.Sql.Tests/SnapshotSchemaTests.cs`

**Step 1: Write schema definition with SQL for both databases**

```csharp
namespace ZeroAlloc.EventSourcing.Sql;

/// <summary>
/// Shared snapshot table schema for PostgreSQL and SQL Server.
/// </summary>
public static class SnapshotSchema
{
    /// <summary>
    /// PostgreSQL CREATE TABLE statement for snapshots table.
    /// </summary>
    public const string PostgreSqlCreateTable = @"
        CREATE TABLE IF NOT EXISTS snapshots (
            stream_id       VARCHAR(255)      NOT NULL PRIMARY KEY,
            position        BIGINT            NOT NULL,
            state_type      VARCHAR(500)      NOT NULL,
            payload         BYTEA             NOT NULL,
            created_at      TIMESTAMPTZ       NOT NULL DEFAULT CURRENT_TIMESTAMP
        );
    ";

    /// <summary>
    /// SQL Server CREATE TABLE statement for snapshots table.
    /// </summary>
    public const string SqlServerCreateTable = @"
        IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'snapshots')
        BEGIN
            CREATE TABLE snapshots (
                stream_id       VARCHAR(255)      NOT NULL PRIMARY KEY,
                position        BIGINT            NOT NULL,
                state_type      VARCHAR(500)      NOT NULL,
                payload         VARBINARY(MAX)    NOT NULL,
                created_at      DATETIMEOFFSET    NOT NULL DEFAULT SYSDATETIMEOFFSET()
            );
        END
    ";

    /// <summary>
    /// Creates the snapshots table in PostgreSQL.
    /// Idempotent: safe to call multiple times.
    /// </summary>
    public static async ValueTask EnsurePostgreSqlSchemaAsync(
        NpgsqlDataSource dataSource,
        CancellationToken ct = default)
    {
        using var connection = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = PostgreSqlCreateTable;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates the snapshots table in SQL Server.
    /// Idempotent: safe to call multiple times.
    /// </summary>
    public static async ValueTask EnsureSqlServerSchemaAsync(
        string connectionString,
        CancellationToken ct = default)
    {
        using var connection = new System.Data.SqlClient.SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = SqlServerCreateTable;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
```

**Step 2: Write unit tests for schema constants**

```csharp
using FluentAssertions;
using ZeroAlloc.EventSourcing.Sql;

namespace ZeroAlloc.EventSourcing.Sql.Tests;

public sealed class SnapshotSchemaTests
{
    [Fact]
    public void PostgreSqlCreateTable_Contains_StreamIdColumn()
    {
        SnapshotSchema.PostgreSqlCreateTable.Should().Contain("stream_id");
    }

    [Fact]
    public void PostgreSqlCreateTable_Contains_PositionColumn()
    {
        SnapshotSchema.PostgreSqlCreateTable.Should().Contain("position");
    }

    [Fact]
    public void PostgreSqlCreateTable_Contains_StateTypeColumn()
    {
        SnapshotSchema.PostgreSqlCreateTable.Should().Contain("state_type");
    }

    [Fact]
    public void PostgreSqlCreateTable_Contains_PayloadAsBytes()
    {
        SnapshotSchema.PostgreSqlCreateTable.Should().Contain("BYTEA");
    }

    [Fact]
    public void SqlServerCreateTable_Contains_StreamIdColumn()
    {
        SnapshotSchema.SqlServerCreateTable.Should().Contain("stream_id");
    }

    [Fact]
    public void SqlServerCreateTable_Contains_PayloadAsVarbinary()
    {
        SnapshotSchema.SqlServerCreateTable.Should().Contain("VARBINARY");
    }

    [Theory]
    [InlineData("stream_id", "position", "state_type", "payload", "created_at")]
    public void PostgreSqlCreateTable_HasAllRequiredColumns(params string[] columns)
    {
        var schema = SnapshotSchema.PostgreSqlCreateTable;
        foreach (var column in columns)
        {
            schema.Should().Contain(column);
        }
    }
}
```

**Step 3: Run tests**

Run: `dotnet test tests/ZeroAlloc.EventSourcing.Sql.Tests/SnapshotSchemaTests.cs -v normal`
Expected: PASS (6 tests)

**Step 4: Commit**

```bash
git add src/ZeroAlloc.EventSourcing.Sql/SnapshotSchema.cs tests/ZeroAlloc.EventSourcing.Sql.Tests/SnapshotSchemaTests.cs
git commit -m "feat: add shared snapshot table schema for PostgreSQL and SQL Server"
```

---

## Task 2: Implement PostgreSqlSnapshotStore<TState>

**Files:**
- Create: `src/ZeroAlloc.EventSourcing.Sql/PostgreSqlSnapshotStore.cs`
- Test: `tests/ZeroAlloc.EventSourcing.Sql.Tests/PostgreSqlSnapshotStoreTests.cs`

**Step 1: Write unit tests (TDD)**

```csharp
using FluentAssertions;
using Npgsql;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Sql;

namespace ZeroAlloc.EventSourcing.Sql.Tests;

public sealed class PostgreSqlSnapshotStoreTests : IAsyncLifetime
{
    private NpgsqlDataSource _dataSource = null!;
    private PostgreSqlSnapshotStore<OrderState> _store = null!;

    public async Task InitializeAsync()
    {
        // Use testcontainers PostgreSQL for integration tests
        // For now, create in-memory mock for unit tests
        _dataSource = NpgsqlDataSource.Create("Host=localhost;Database=test;Username=postgres;Password=postgres");
        _store = new PostgreSqlSnapshotStore<OrderState>(_dataSource);
    }

    public async Task DisposeAsync()
    {
        await _dataSource.DisposeAsync();
    }

    [Fact]
    public async Task Constructor_NullDataSource_ThrowsArgumentNullException()
    {
        FluentActions.Invoking(() => new PostgreSqlSnapshotStore<OrderState>(null!))
            .Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task EnsureSchemaAsync_CreatesTable()
    {
        // Arrange & Act
        await _store.EnsureSchemaAsync();

        // Assert
        var connection = await _dataSource.OpenConnectionAsync();
        using (connection)
        {
            var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM information_schema.tables WHERE table_name = 'snapshots'";
            var count = (long)await command.ExecuteScalarAsync();
            count.Should().Be(1);
        }
    }

    [Fact]
    public async Task ReadAsync_NoSnapshot_ReturnsNull()
    {
        // Arrange
        await _store.EnsureSchemaAsync();
        var streamId = new StreamId("order-123");

        // Act
        var result = await _store.ReadAsync(streamId);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task WriteAsync_ThenReadAsync_ReturnsWrittenState()
    {
        // Arrange
        await _store.EnsureSchemaAsync();
        var streamId = new StreamId("order-456");
        var position = new StreamPosition(5);
        var state = new OrderState { Amount = 100m, Status = "Shipped" };

        // Act
        await _store.WriteAsync(streamId, position, state);
        var result = await _store.ReadAsync(streamId);

        // Assert
        result.Should().NotBeNull();
        result.Value.Position.Should().Be(position);
        result.Value.State.Amount.Should().Be(100m);
        result.Value.State.Status.Should().Be("Shipped");
    }

    [Fact]
    public async Task WriteAsync_Upsert_LastOneWins()
    {
        // Arrange
        await _store.EnsureSchemaAsync();
        var streamId = new StreamId("order-789");
        var state1 = new OrderState { Amount = 50m };
        var state2 = new OrderState { Amount = 150m };

        // Act
        await _store.WriteAsync(streamId, new StreamPosition(2), state1);
        await _store.WriteAsync(streamId, new StreamPosition(5), state2);
        var result = await _store.ReadAsync(streamId);

        // Assert
        result.Value.Position.Should().Be(new StreamPosition(5));
        result.Value.State.Amount.Should().Be(150m);
    }
}
```

**Step 2: Implement PostgreSqlSnapshotStore<TState>**

```csharp
using Npgsql;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.Results;

namespace ZeroAlloc.EventSourcing.Sql;

/// <summary>
/// PostgreSQL implementation of <see cref="ISnapshotStore{TState}"/>.
/// Uses BYTEA for payload storage and ON CONFLICT for atomic upsert.
/// </summary>
public sealed class PostgreSqlSnapshotStore<TState> : ISnapshotStore<TState>
    where TState : struct, IAggregateState<TState>
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IEventSerializer _serializer;
    private readonly string _expectedStateType = typeof(TState).FullName!;

    public PostgreSqlSnapshotStore(
        NpgsqlDataSource dataSource,
        IEventSerializer? serializer = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
        _serializer = serializer ?? new DefaultEventSerializer();
    }

    public async ValueTask EnsureSchemaAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await SnapshotSchema.EnsurePostgreSqlSchemaAsync(_dataSource, ct).ConfigureAwait(false);
    }

    public async ValueTask<(StreamPosition, TState)?> ReadAsync(
        StreamId streamId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT position, state_type, payload
            FROM snapshots
            WHERE stream_id = @stream_id";

        command.Parameters.AddWithValue("@stream_id", streamId.Value);

        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var position = new StreamPosition((long)reader[0]);
            var stateType = (string)reader[1];
            var payload = (byte[])reader[2];

            if (stateType != _expectedStateType)
                return null; // Type mismatch, treat as missing

            var state = (TState)_serializer.Deserialize(payload, typeof(TState));
            return (position, state);
        }

        return null;
    }

    public async ValueTask WriteAsync(
        StreamId streamId,
        StreamPosition position,
        TState state,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var payload = _serializer.Serialize(state);
        var stateType = typeof(TState).FullName!;

        using var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO snapshots (stream_id, position, state_type, payload, created_at)
            VALUES (@stream_id, @position, @state_type, @payload, CURRENT_TIMESTAMP)
            ON CONFLICT (stream_id) DO UPDATE SET
                position = EXCLUDED.position,
                state_type = EXCLUDED.state_type,
                payload = EXCLUDED.payload,
                created_at = CURRENT_TIMESTAMP";

        command.Parameters.AddWithValue("@stream_id", streamId.Value);
        command.Parameters.AddWithValue("@position", position.Value);
        command.Parameters.AddWithValue("@state_type", stateType);
        command.Parameters.AddWithValue("@payload", payload);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
```

**Step 3: Run tests**

Run: `dotnet test tests/ZeroAlloc.EventSourcing.Sql.Tests/PostgreSqlSnapshotStoreTests.cs -v normal`
Expected: PASS (6 tests)

**Step 4: Commit**

```bash
git add src/ZeroAlloc.EventSourcing.Sql/PostgreSqlSnapshotStore.cs tests/ZeroAlloc.EventSourcing.Sql.Tests/PostgreSqlSnapshotStoreTests.cs
git commit -m "feat: implement PostgreSqlSnapshotStore with BYTEA payload and ON CONFLICT upsert"
```

---

## Task 3: Implement SqlServerSnapshotStore<TState>

**Files:**
- Create: `src/ZeroAlloc.EventSourcing.Sql/SqlServerSnapshotStore.cs`
- Test: `tests/ZeroAlloc.EventSourcing.Sql.Tests/SqlServerSnapshotStoreTests.cs`

**Step 1: Write unit tests (TDD)**

```csharp
using FluentAssertions;
using System.Data.SqlClient;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Sql;

namespace ZeroAlloc.EventSourcing.Sql.Tests;

public sealed class SqlServerSnapshotStoreTests : IAsyncLifetime
{
    private string _connectionString = null!;
    private SqlServerSnapshotStore<OrderState> _store = null!;

    public async Task InitializeAsync()
    {
        _connectionString = "Server=localhost;Database=test;User Id=sa;Password=YourPassword123";
        _store = new SqlServerSnapshotStore<OrderState>(_connectionString);
    }

    public async Task DisposeAsync()
    {
        // Cleanup if needed
    }

    [Fact]
    public void Constructor_NullConnectionString_ThrowsArgumentNullException()
    {
        FluentActions.Invoking(() => new SqlServerSnapshotStore<OrderState>(null!))
            .Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task EnsureSchemaAsync_CreatesTable()
    {
        // Arrange & Act
        await _store.EnsureSchemaAsync();

        // Assert
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT COUNT(*) FROM information_schema.tables
            WHERE table_name = 'snapshots'";
        var count = (int)await command.ExecuteScalarAsync();
        count.Should().Be(1);
    }

    [Fact]
    public async Task ReadAsync_NoSnapshot_ReturnsNull()
    {
        // Arrange
        await _store.EnsureSchemaAsync();
        var streamId = new StreamId("invoice-111");

        // Act
        var result = await _store.ReadAsync(streamId);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task WriteAsync_ThenReadAsync_ReturnsWrittenState()
    {
        // Arrange
        await _store.EnsureSchemaAsync();
        var streamId = new StreamId("invoice-222");
        var position = new StreamPosition(3);
        var state = new InvoiceState { Amount = 500m, Status = "Paid" };

        // Act
        await _store.WriteAsync(streamId, position, state);
        var result = await _store.ReadAsync(streamId);

        // Assert
        result.Should().NotBeNull();
        result.Value.Position.Should().Be(position);
        result.Value.State.Amount.Should().Be(500m);
        result.Value.State.Status.Should().Be("Paid");
    }

    [Fact]
    public async Task WriteAsync_Upsert_LastOneWins()
    {
        // Arrange
        await _store.EnsureSchemaAsync();
        var streamId = new StreamId("invoice-333");
        var state1 = new InvoiceState { Amount = 200m };
        var state2 = new InvoiceState { Amount = 300m };

        // Act
        await _store.WriteAsync(streamId, new StreamPosition(1), state1);
        await _store.WriteAsync(streamId, new StreamPosition(2), state2);
        var result = await _store.ReadAsync(streamId);

        // Assert
        result.Value.Position.Should().Be(new StreamPosition(2));
        result.Value.State.Amount.Should().Be(300m);
    }
}
```

**Step 2: Implement SqlServerSnapshotStore<TState>**

```csharp
using System.Data.SqlClient;
using ZeroAlloc.EventSourcing;

namespace ZeroAlloc.EventSourcing.Sql;

/// <summary>
/// SQL Server implementation of <see cref="ISnapshotStore{TState}"/>.
/// Uses VARBINARY(MAX) for payload storage and MERGE for atomic upsert.
/// </summary>
public sealed class SqlServerSnapshotStore<TState> : ISnapshotStore<TState>
    where TState : struct, IAggregateState<TState>
{
    private readonly string _connectionString;
    private readonly IEventSerializer _serializer;
    private readonly string _expectedStateType = typeof(TState).FullName!;

    public SqlServerSnapshotStore(
        string connectionString,
        IEventSerializer? serializer = null)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("Connection string cannot be empty", nameof(connectionString));

        _connectionString = connectionString;
        _serializer = serializer ?? new DefaultEventSerializer();
    }

    public async ValueTask EnsureSchemaAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await SnapshotSchema.EnsureSqlServerSchemaAsync(_connectionString, ct).ConfigureAwait(false);
    }

    public async ValueTask<(StreamPosition, TState)?> ReadAsync(
        StreamId streamId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT position, state_type, payload
            FROM snapshots
            WHERE stream_id = @stream_id";

        command.Parameters.AddWithValue("@stream_id", streamId.Value);

        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var position = new StreamPosition((long)reader[0]);
            var stateType = (string)reader[1];
            var payload = (byte[])reader[2];

            if (stateType != _expectedStateType)
                return null; // Type mismatch, treat as missing

            var state = (TState)_serializer.Deserialize(payload, typeof(TState));
            return (position, state);
        }

        return null;
    }

    public async ValueTask WriteAsync(
        StreamId streamId,
        StreamPosition position,
        TState state,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var payload = _serializer.Serialize(state);
        var stateType = typeof(TState).FullName!;

        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = @"
            MERGE INTO snapshots AS target
            USING (SELECT @stream_id AS stream_id) AS source
            ON target.stream_id = source.stream_id
            WHEN MATCHED THEN
                UPDATE SET
                    position = @position,
                    state_type = @state_type,
                    payload = @payload,
                    created_at = SYSDATETIMEOFFSET()
            WHEN NOT MATCHED THEN
                INSERT (stream_id, position, state_type, payload, created_at)
                VALUES (@stream_id, @position, @state_type, @payload, SYSDATETIMEOFFSET());";

        command.Parameters.AddWithValue("@stream_id", streamId.Value);
        command.Parameters.AddWithValue("@position", position.Value);
        command.Parameters.AddWithValue("@state_type", stateType);
        command.Parameters.AddWithValue("@payload", payload);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
```

**Step 3: Run tests**

Run: `dotnet test tests/ZeroAlloc.EventSourcing.Sql.Tests/SqlServerSnapshotStoreTests.cs -v normal`
Expected: PASS (6 tests)

**Step 4: Commit**

```bash
git add src/ZeroAlloc.EventSourcing.Sql/SqlServerSnapshotStore.cs tests/ZeroAlloc.EventSourcing.Sql.Tests/SqlServerSnapshotStoreTests.cs
git commit -m "feat: implement SqlServerSnapshotStore with VARBINARY payload and MERGE upsert"
```

---

## Task 4: Create ISnapshotStoreProvider Factory Interface

**Files:**
- Create: `src/ZeroAlloc.EventSourcing.Sql/ISnapshotStoreProvider.cs`
- Create: `src/ZeroAlloc.EventSourcing.Sql/SnapshotStoreProvider.cs`
- Test: `tests/ZeroAlloc.EventSourcing.Sql.Tests/SnapshotStoreProviderTests.cs`

**Step 1: Write interface and unit tests**

```csharp
using FluentAssertions;
using Npgsql;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Sql;

namespace ZeroAlloc.EventSourcing.Sql.Tests;

public sealed class SnapshotStoreProviderTests
{
    [Fact]
    public async Task CreateAsync_PostgreSQL_ReturnsPostgreSqlSnapshotStore()
    {
        // Arrange
        var provider = new SnapshotStoreProvider(
            database: "PostgreSQL",
            connectionString: "Host=localhost;Database=test;Username=postgres;Password=postgres");

        // Act
        var store = await provider.CreateAsync<OrderState>();

        // Assert
        store.Should().BeOfType<PostgreSqlSnapshotStore<OrderState>>();
    }

    [Fact]
    public async Task CreateAsync_SqlServer_ReturnsSqlServerSnapshotStore()
    {
        // Arrange
        var provider = new SnapshotStoreProvider(
            database: "SqlServer",
            connectionString: "Server=localhost;Database=test;User Id=sa;Password=Password");

        // Act
        var store = await provider.CreateAsync<OrderState>();

        // Assert
        store.Should().BeOfType<SqlServerSnapshotStore<OrderState>>();
    }

    [Fact]
    public void Constructor_InvalidDatabase_ThrowsArgumentException()
    {
        FluentActions.Invoking(() => new SnapshotStoreProvider(
            database: "MongoDB",
            connectionString: "..."))
            .Should().Throw<ArgumentException>()
            .WithMessage("*PostgreSQL*SqlServer*");
    }

    [Fact]
    public void Constructor_NullConnectionString_ThrowsArgumentNullException()
    {
        FluentActions.Invoking(() => new SnapshotStoreProvider(
            database: "PostgreSQL",
            connectionString: null!))
            .Should().Throw<ArgumentNullException>();
    }
}
```

**Step 2: Implement factory interface and provider**

```csharp
namespace ZeroAlloc.EventSourcing.Sql;

/// <summary>
/// Factory for creating database-specific snapshot stores at runtime.
/// </summary>
public interface ISnapshotStoreProvider
{
    ValueTask<ISnapshotStore<TState>> CreateAsync<TState>(CancellationToken ct = default)
        where TState : struct, IAggregateState<TState>;
}

/// <summary>
/// Runtime snapshot store factory supporting PostgreSQL and SQL Server.
/// </summary>
public sealed class SnapshotStoreProvider : ISnapshotStoreProvider
{
    private readonly string _database;
    private readonly string _connectionString;

    public SnapshotStoreProvider(string database, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(connectionString);

        if (!database.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase) &&
            !database.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Database must be 'PostgreSQL' or 'SqlServer', got '{database}'",
                nameof(database));
        }

        _database = database;
        _connectionString = connectionString;
    }

    public async ValueTask<ISnapshotStore<TState>> CreateAsync<TState>(
        CancellationToken ct = default)
        where TState : struct, IAggregateState<TState>
    {
        ct.ThrowIfCancellationRequested();

        if (_database.Equals("PostgreSQL", StringComparison.OrdinalIgnoreCase))
        {
            var dataSource = Npgsql.NpgsqlDataSource.Create(_connectionString);
            return new PostgreSqlSnapshotStore<TState>(dataSource);
        }
        else if (_database.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            return new SqlServerSnapshotStore<TState>(_connectionString);
        }

        throw new InvalidOperationException($"Unsupported database: {_database}");
    }
}
```

**Step 3: Run tests**

Run: `dotnet test tests/ZeroAlloc.EventSourcing.Sql.Tests/SnapshotStoreProviderTests.cs -v normal`
Expected: PASS (4 tests)

**Step 4: Commit**

```bash
git add src/ZeroAlloc.EventSourcing.Sql/ISnapshotStoreProvider.cs src/ZeroAlloc.EventSourcing.Sql/SnapshotStoreProvider.cs tests/ZeroAlloc.EventSourcing.Sql.Tests/SnapshotStoreProviderTests.cs
git commit -m "feat: add ISnapshotStoreProvider factory for runtime database selection"
```

---

## Task 5: Add Contract Tests (Shared Test Base)

**Files:**
- Create: `tests/ZeroAlloc.EventSourcing.Sql.Tests/SnapshotStoreContractTests.cs`
- Modify: `tests/ZeroAlloc.EventSourcing.Sql.Tests/PostgreSqlSnapshotStoreTests.cs` (inherit from contract)
- Modify: `tests/ZeroAlloc.EventSourcing.Sql.Tests/SqlServerSnapshotStoreTests.cs` (inherit from contract)

**Step 1: Create abstract contract test base**

```csharp
using FluentAssertions;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Sql;

namespace ZeroAlloc.EventSourcing.Sql.Tests;

/// <summary>
/// Contract tests ensuring all ISnapshotStore implementations behave identically.
/// </summary>
public abstract class SnapshotStoreContractTests<TStore> : IAsyncLifetime
    where TStore : ISnapshotStore<OrderState>
{
    protected abstract Task<TStore> CreateStoreAsync(CancellationToken ct = default);

    protected TStore _store = null!;

    public async Task InitializeAsync()
    {
        _store = await CreateStoreAsync();
        await _store.EnsureSchemaAsync();
    }

    public abstract Task DisposeAsync();

    [Fact]
    public async Task RoundTrip_WriteAndRead_ReturnsSameState()
    {
        // Arrange
        var streamId = new StreamId("contract-test-1");
        var position = new StreamPosition(7);
        var state = new OrderState { Amount = 777m, Status = "Verified" };

        // Act
        await _store.WriteAsync(streamId, position, state);
        var result = await _store.ReadAsync(streamId);

        // Assert
        result.Should().NotBeNull();
        result.Value.Position.Should().Be(position);
        result.Value.State.Amount.Should().Be(state.Amount);
        result.Value.State.Status.Should().Be(state.Status);
    }

    [Fact]
    public async Task MultipleStreams_IsolatedSnapshots()
    {
        // Arrange
        var stream1 = new StreamId("stream-1");
        var stream2 = new StreamId("stream-2");
        var state1 = new OrderState { Amount = 100m };
        var state2 = new OrderState { Amount = 200m };

        // Act
        await _store.WriteAsync(stream1, new StreamPosition(1), state1);
        await _store.WriteAsync(stream2, new StreamPosition(2), state2);
        var result1 = await _store.ReadAsync(stream1);
        var result2 = await _store.ReadAsync(stream2);

        // Assert
        result1.Value.State.Amount.Should().Be(100m);
        result2.Value.State.Amount.Should().Be(200m);
    }

    [Fact]
    public async Task Upsert_LastWriteWins()
    {
        // Arrange
        var streamId = new StreamId("contract-upsert");
        var state1 = new OrderState { Amount = 50m };
        var state2 = new OrderState { Amount = 150m };

        // Act
        await _store.WriteAsync(streamId, new StreamPosition(2), state1);
        await _store.WriteAsync(streamId, new StreamPosition(5), state2);
        var result = await _store.ReadAsync(streamId);

        // Assert - Latest write should win
        result.Value.Position.Should().Be(new StreamPosition(5));
        result.Value.State.Amount.Should().Be(150m);
    }
}
```

**Step 2: Inherit contract tests in PostgreSQL tests**

Modify PostgreSqlSnapshotStoreTests to inherit from SnapshotStoreContractTests<PostgreSqlSnapshotStore<OrderState>>

**Step 3: Inherit contract tests in SQL Server tests**

Modify SqlServerSnapshotStoreTests to inherit from SnapshotStoreContractTests<SqlServerSnapshotStore<OrderState>>

**Step 4: Run tests**

Run: `dotnet test tests/ZeroAlloc.EventSourcing.Sql.Tests/ -v normal`
Expected: PASS (all contract tests passing for both PostgreSQL and SQL Server)

**Step 5: Commit**

```bash
git add tests/ZeroAlloc.EventSourcing.Sql.Tests/SnapshotStoreContractTests.cs tests/ZeroAlloc.EventSourcing.Sql.Tests/PostgreSqlSnapshotStoreTests.cs tests/ZeroAlloc.EventSourcing.Sql.Tests/SqlServerSnapshotStoreTests.cs
git commit -m "test: add contract tests ensuring all ISnapshotStore implementations behave identically"
```

---

## Task 6: End-to-End Integration Tests with Testcontainers

**Files:**
- Create: `tests/ZeroAlloc.EventSourcing.Sql.Tests/PostgreSqlSnapshotStoreIntegrationTests.cs`
- Create: `tests/ZeroAlloc.EventSourcing.Sql.Tests/SqlServerSnapshotStoreIntegrationTests.cs`

**Step 1: Write PostgreSQL integration test with Testcontainers**

```csharp
using FluentAssertions;
using Npgsql;
using Testcontainers.PostgreSql;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Sql;

namespace ZeroAlloc.EventSourcing.Sql.Tests;

[Collection("PostgreSQL")]
public sealed class PostgreSqlSnapshotStoreIntegrationTests : IAsyncLifetime
{
    private PostgreSqlContainer _container = null!;
    private NpgsqlDataSource _dataSource = null!;
    private PostgreSqlSnapshotStore<OrderState> _store = null!;

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder()
            .WithDatabase("snapshot_test")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

        await _container.StartAsync();

        var connectionString = _container.GetConnectionString();
        _dataSource = NpgsqlDataSource.Create(connectionString);
        _store = new PostgreSqlSnapshotStore<OrderState>(_dataSource);
    }

    public async Task DisposeAsync()
    {
        await _container.StopAsync();
        await _dataSource.DisposeAsync();
    }

    [Fact]
    public async Task PostgreSQL_RoundTrip_WithRealDatabase()
    {
        // Arrange
        await _store.EnsureSchemaAsync();
        var streamId = new StreamId("pg-order-123");
        var position = new StreamPosition(10);
        var state = new OrderState { Amount = 999.99m, Status = "Completed" };

        // Act
        await _store.WriteAsync(streamId, position, state);
        var result = await _store.ReadAsync(streamId);

        // Assert
        result.Should().NotBeNull();
        result.Value.Position.Should().Be(position);
        result.Value.State.Amount.Should().Be(999.99m);
        result.Value.State.Status.Should().Be("Completed");
    }

    [Fact]
    public async Task PostgreSQL_LargePayload_Handles()
    {
        // Arrange
        await _store.EnsureSchemaAsync();
        var streamId = new StreamId("pg-large");
        var largeState = new OrderState { Amount = 1_000_000m, Status = new string('x', 10000) };

        // Act
        await _store.WriteAsync(streamId, new StreamPosition(1), largeState);
        var result = await _store.ReadAsync(streamId);

        // Assert
        result.Value.State.Amount.Should().Be(1_000_000m);
        result.Value.State.Status.Length.Should().Be(10000);
    }
}
```

**Step 2: Write SQL Server integration test with Testcontainers**

```csharp
using FluentAssertions;
using System.Data.SqlClient;
using Testcontainers.MsSql;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Sql;

namespace ZeroAlloc.EventSourcing.Sql.Tests;

[Collection("SqlServer")]
public sealed class SqlServerSnapshotStoreIntegrationTests : IAsyncLifetime
{
    private MsSqlContainer _container = null!;
    private SqlServerSnapshotStore<InvoiceState> _store = null!;

    public async Task InitializeAsync()
    {
        _container = new MsSqlBuilder()
            .WithPassword("Snapshot@123")
            .Build();

        await _container.StartAsync();

        var connectionString = _container.GetConnectionString();
        _store = new SqlServerSnapshotStore<InvoiceState>(connectionString);
    }

    public async Task DisposeAsync()
    {
        await _container.StopAsync();
    }

    [Fact]
    public async Task SqlServer_RoundTrip_WithRealDatabase()
    {
        // Arrange
        await _store.EnsureSchemaAsync();
        var streamId = new StreamId("mssql-invoice-456");
        var position = new StreamPosition(5);
        var state = new InvoiceState { Amount = 5000m, Status = "Invoiced" };

        // Act
        await _store.WriteAsync(streamId, position, state);
        var result = await _store.ReadAsync(streamId);

        // Assert
        result.Should().NotBeNull();
        result.Value.Position.Should().Be(position);
        result.Value.State.Amount.Should().Be(5000m);
        result.Value.State.Status.Should().Be("Invoiced");
    }

    [Fact]
    public async Task SqlServer_LargePayload_Handles()
    {
        // Arrange
        await _store.EnsureSchemaAsync();
        var streamId = new StreamId("mssql-large");
        var largeState = new InvoiceState { Amount = 2_000_000m, Status = new string('y', 20000) };

        // Act
        await _store.WriteAsync(streamId, new StreamPosition(1), largeState);
        var result = await _store.ReadAsync(streamId);

        // Assert
        result.Value.State.Amount.Should().Be(2_000_000m);
        result.Value.State.Status.Length.Should().Be(20000);
    }
}
```

**Step 3: Run integration tests**

Run: `dotnet test tests/ZeroAlloc.EventSourcing.Sql.Tests/PostgreSqlSnapshotStoreIntegrationTests.cs tests/ZeroAlloc.EventSourcing.Sql.Tests/SqlServerSnapshotStoreIntegrationTests.cs -v normal`
Expected: PASS (4 integration tests, may take 30-60s for containers to start)

**Step 4: Commit**

```bash
git add tests/ZeroAlloc.EventSourcing.Sql.Tests/PostgreSqlSnapshotStoreIntegrationTests.cs tests/ZeroAlloc.EventSourcing.Sql.Tests/SqlServerSnapshotStoreIntegrationTests.cs
git commit -m "test: add end-to-end integration tests for PostgreSQL and SQL Server snapshot stores"
```

---

## Task 7: Add Documentation & DI Configuration Examples

**Files:**
- Create: `docs/examples/SqlSnapshotStores.md`

**Step 1: Create documentation**

```markdown
# SQL Snapshot Stores — PostgreSQL & SQL Server

## Overview

Phase 6 provides production-grade snapshot persistence for both PostgreSQL and SQL Server databases. Both implementations use optimized SQL patterns while conforming to the same `ISnapshotStore<TState>` interface, allowing seamless switching between databases.

## PostgreSQL Implementation

### Basic Usage

```csharp
// Create data source
var dataSource = NpgsqlDataSource.Create("Host=localhost;Database=event_store;Username=postgres");

// Create snapshot store
var snapshotStore = new PostgreSqlSnapshotStore<OrderState>(dataSource);

// Ensure schema exists (idempotent)
await snapshotStore.EnsureSchemaAsync();

// Write snapshot
await snapshotStore.WriteAsync(
    new StreamId("order-123"),
    new StreamPosition(50),
    orderState);

// Read snapshot
var snapshot = await snapshotStore.ReadAsync(new StreamId("order-123"));
```

### Database-Specific Details

- **Payload Type:** `BYTEA` (PostgreSQL binary type)
- **Upsert Strategy:** `ON CONFLICT (stream_id) DO UPDATE` (atomic, no race conditions)
- **Timestamp:** `TIMESTAMPTZ` (timezone-aware)

## SQL Server Implementation

### Basic Usage

```csharp
// Create snapshot store
var snapshotStore = new SqlServerSnapshotStore<OrderState>(
    "Server=localhost;Database=event_store;User Id=sa;Password=YourPassword");

// Ensure schema exists (idempotent)
await snapshotStore.EnsureSchemaAsync();

// Write snapshot
await snapshotStore.WriteAsync(
    new StreamId("invoice-456"),
    new StreamPosition(30),
    invoiceState);

// Read snapshot
var snapshot = await snapshotStore.ReadAsync(new StreamId("invoice-456"));
```

### Database-Specific Details

- **Payload Type:** `VARBINARY(MAX)` (SQL Server binary type)
- **Upsert Strategy:** `MERGE INTO` (atomic, standard SQL)
- **Timestamp:** `DATETIMEOFFSET` (timezone-aware)

## Runtime Database Selection

Use `SnapshotStoreProvider` to choose databases at runtime:

```csharp
var provider = new SnapshotStoreProvider(
    database: Environment.GetEnvironmentVariable("SNAPSHOT_DB") ?? "PostgreSQL",
    connectionString: Environment.GetEnvironmentVariable("SNAPSHOT_CONNECTION_STRING")!);

var snapshotStore = await provider.CreateAsync<OrderState>();
```

## DI Configuration

### For PostgreSQL

```csharp
services
    .AddScoped<ISnapshotStore<OrderState>>(sp =>
        new PostgreSqlSnapshotStore<OrderState>(
            NpgsqlDataSource.Create(
                sp.GetRequiredService<IConfiguration>()["Database:PostgreSQL:ConnectionString"])));
```

### For SQL Server

```csharp
services
    .AddScoped<ISnapshotStore<OrderState>>(sp =>
        new SqlServerSnapshotStore<OrderState>(
            sp.GetRequiredService<IConfiguration>()["Database:SqlServer:ConnectionString"]));
```

### Using Factory (Recommended)

```csharp
services
    .AddScoped<ISnapshotStoreProvider>(sp =>
        new SnapshotStoreProvider(
            database: sp.GetRequiredService<IConfiguration>()["Snapshot:Database"],
            connectionString: sp.GetRequiredService<IConfiguration>()["Snapshot:ConnectionString"]))
    .AddScoped<ISnapshotStore<OrderState>>(sp =>
        sp.GetRequiredService<ISnapshotStoreProvider>().CreateAsync<OrderState>().Result);
```

## Schema

Both databases use the same logical schema:

| Column | Type | Purpose |
|--------|------|---------|
| `stream_id` | VARCHAR(255) | Stream identifier (PRIMARY KEY) |
| `position` | BIGINT | Event position when snapshot taken |
| `state_type` | VARCHAR(500) | CLR type name (for validation) |
| `payload` | BYTEA / VARBINARY(MAX) | Serialized aggregate state |
| `created_at` | TIMESTAMPTZ / DATETIMEOFFSET | Snapshot creation timestamp |

## Integration with Snapshot-Optimized Loading

Use Phase 5.1 decorator with Phase 6 stores:

```csharp
// Create inner repository
var innerRepo = new AggregateRepository<Order, OrderId>(
    eventStore,
    () => new Order(),
    id => new StreamId($"order-{id.Value}"));

// Create SQL snapshot store (PostgreSQL or SQL Server)
var snapshotStore = new PostgreSqlSnapshotStore<OrderState>(dataSource);
await snapshotStore.EnsureSchemaAsync();

// Wrap with decorator
var snapshotRepo = new SnapshotCachingRepositoryDecorator<Order, OrderId, OrderState>(
    innerRepository: innerRepo,
    snapshotStore: snapshotStore,
    strategy: SnapshotLoadingStrategy.ValidateAndReplay,
    getState: o => o.State,
    setState: (o, state, pos) => o.RestoreState(state, pos),
    streamIdFactory: id => new StreamId($"order-{id.Value}"),
    eventStore: eventStore);
```

## Testcontainers for Local Development

For local testing without installing PostgreSQL/SQL Server:

```csharp
// PostgreSQL
var pgContainer = new PostgreSqlBuilder().Build();
await pgContainer.StartAsync();
var dataSource = NpgsqlDataSource.Create(pgContainer.GetConnectionString());

// SQL Server
var sqlContainer = new MsSqlBuilder().Build();
await sqlContainer.StartAsync();
var connectionString = sqlContainer.GetConnectionString();
```
```

**Step 2: Commit documentation**

Run: (no test needed for documentation)

```bash
git add docs/examples/SqlSnapshotStores.md
git commit -m "docs: add SQL snapshot stores documentation and configuration examples"
```

---

## Testing Checklist

After all tasks complete, run full verification:

```bash
# Run all Phase 6 tests
dotnet test tests/ZeroAlloc.EventSourcing.Sql.Tests/ -v normal

# Run full suite (excluding slow database tests if needed)
dotnet test --filter "FullyQualifiedName!~Integration" -v normal

# Verify all commits
git log --oneline -10 | head -7  # Should show Phase 6 commits
```

**Expected Results:**
- ✅ 20+ unit tests passing (schema, PostgreSQL, SQL Server, provider, contracts)
- ✅ 4 integration tests passing (PostgreSQL + SQL Server, with real containers)
- ✅ 0 regressions in existing tests
- ✅ Documentation complete
- ✅ 7 clean commits with semantic messages

---

## Success Criteria

- ✅ PostgreSQL and SQL Server implementations both work
- ✅ Both conform to `ISnapshotStore<TState>` interface
- ✅ Snapshot data survives persistence and deserialization
- ✅ Last-write-wins semantics (upsert behavior)
- ✅ `EnsureSchemaAsync` is idempotent
- ✅ Integration tests with real databases pass
- ✅ Contract tests ensure behavior consistency
- ✅ Runtime database selection via `ISnapshotStoreProvider`
- ✅ Documentation complete with configuration examples
- ✅ All commits clean and semantic
