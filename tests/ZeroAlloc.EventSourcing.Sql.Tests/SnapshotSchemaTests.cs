using FluentAssertions;
using Npgsql;
using ZeroAlloc.EventSourcing.Sql;

namespace ZeroAlloc.EventSourcing.Sql.Tests;

public class SnapshotSchemaTests
{
    [Fact]
    public void PostgreSqlCreateTable_Contains_StreamId()
    {
        SnapshotSchema.PostgreSqlCreateTable.Should().Contain("stream_id");
    }

    [Fact]
    public void PostgreSqlCreateTable_Contains_Position()
    {
        SnapshotSchema.PostgreSqlCreateTable.Should().Contain("position");
    }

    [Fact]
    public void PostgreSqlCreateTable_Contains_StateType()
    {
        SnapshotSchema.PostgreSqlCreateTable.Should().Contain("state_type");
    }

    [Fact]
    public void PostgreSqlCreateTable_Contains_Bytea_Not_Varbinary()
    {
        SnapshotSchema.PostgreSqlCreateTable.Should().Contain("BYTEA");
        SnapshotSchema.PostgreSqlCreateTable.Should().NotContain("VARBINARY");
    }

    [Fact]
    public void SqlServerCreateTable_Contains_StreamId()
    {
        SnapshotSchema.SqlServerCreateTable.Should().Contain("stream_id");
    }

    [Fact]
    public void SqlServerCreateTable_Contains_Varbinary_Not_Bytea()
    {
        SnapshotSchema.SqlServerCreateTable.Should().Contain("VARBINARY");
        SnapshotSchema.SqlServerCreateTable.Should().NotContain("BYTEA");
    }

    [Fact]
    public async Task EnsurePostgreSqlSchemaAsync_NullDataSource_ThrowsArgumentNullException()
    {
        await FluentActions.Invoking(async () =>
            await SnapshotSchema.EnsurePostgreSqlSchemaAsync(null!))
            .Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task EnsureSqlServerSchemaAsync_NullConnectionString_ThrowsArgumentNullException()
    {
        await FluentActions.Invoking(async () =>
            await SnapshotSchema.EnsureSqlServerSchemaAsync(null!))
            .Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task EnsureSqlServerSchemaAsync_EmptyConnectionString_ThrowsArgumentException()
    {
        await FluentActions.Invoking(async () =>
            await SnapshotSchema.EnsureSqlServerSchemaAsync(""))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task EnsureSqlServerSchemaAsync_WhitespaceConnectionString_ThrowsArgumentException()
    {
        await FluentActions.Invoking(async () =>
            await SnapshotSchema.EnsureSqlServerSchemaAsync("   "))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task EnsurePostgreSqlSchemaAsync_WithCancellation_RespectsToken()
    {
        // Arrange - Create a dataSource (mocked or invalid for quick failure)
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await FluentActions.Invoking(async () =>
            await SnapshotSchema.EnsurePostgreSqlSchemaAsync(
                NpgsqlDataSource.Create("Host=invalid"),
                cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task EnsureSqlServerSchemaAsync_WithCancellation_RespectsToken()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await FluentActions.Invoking(async () =>
            await SnapshotSchema.EnsureSqlServerSchemaAsync(
                "Server=invalid;Database=test",
                cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }
}
