using FluentAssertions;
using Npgsql;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Sql;
using ZeroAlloc.EventSourcing.Tests;

namespace ZeroAlloc.EventSourcing.Sql.Tests;

/// <summary>
/// Contract tests for <see cref="PostgreSqlSnapshotStore{TState}"/>.
/// Inherits all contract tests from <see cref="SnapshotStoreContractTests"/>.
/// </summary>
public class PostgreSqlSnapshotStoreTests : SnapshotStoreContractTests
{
    /// <summary>Test that constructor rejects null dataSource.</summary>
    [Fact]
    public void Constructor_NullDataSource_ThrowsArgumentNullException()
    {
        FluentActions.Invoking(() => new PostgreSqlSnapshotStore<TestState>(null!))
            .Should().Throw<ArgumentNullException>();
    }

    /// <summary>Test that constructor accepts valid dataSource.</summary>
    [Fact]
    public void Constructor_ValidDataSource_Succeeds()
    {
        var dataSource = NpgsqlDataSource.Create("Host=localhost;Database=test;Username=postgres;Password=postgres");

        FluentActions.Invoking(() => new PostgreSqlSnapshotStore<TestState>(dataSource))
            .Should().NotThrow();
    }

    /// <inheritdoc/>
    protected override ISnapshotStore<TestState> CreateStore()
    {
        var dataSource = NpgsqlDataSource.Create("Host=localhost;Database=test;Username=postgres;Password=postgres");
        return new PostgreSqlSnapshotStore<TestState>(dataSource);
    }
}
