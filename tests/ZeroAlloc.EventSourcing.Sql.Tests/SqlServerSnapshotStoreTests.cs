using FluentAssertions;
using ZeroAlloc.EventSourcing;
using ZeroAlloc.EventSourcing.Sql;
using ZeroAlloc.EventSourcing.Tests;

namespace ZeroAlloc.EventSourcing.Sql.Tests;

/// <summary>
/// Contract tests for <see cref="SqlServerSnapshotStore{TState}"/>.
/// Inherits all contract tests from <see cref="SnapshotStoreContractTests"/>.
/// </summary>
public class SqlServerSnapshotStoreTests : SnapshotStoreContractTests
{
    /// <summary>Test that constructor rejects null connection string.</summary>
    [Fact]
    public void Constructor_NullConnectionString_ThrowsArgumentNullException()
    {
        FluentActions.Invoking(() => new SqlServerSnapshotStore<TestState>(null!))
            .Should().Throw<ArgumentNullException>();
    }

    /// <summary>Test that constructor rejects empty connection string.</summary>
    [Fact]
    public void Constructor_EmptyConnectionString_ThrowsArgumentException()
    {
        FluentActions.Invoking(() => new SqlServerSnapshotStore<TestState>(string.Empty))
            .Should().Throw<ArgumentException>();
    }

    /// <summary>Test that constructor rejects whitespace-only connection string.</summary>
    [Fact]
    public void Constructor_WhitespaceConnectionString_ThrowsArgumentException()
    {
        FluentActions.Invoking(() => new SqlServerSnapshotStore<TestState>("   "))
            .Should().Throw<ArgumentException>();
    }

    /// <summary>Test that constructor accepts valid connection string.</summary>
    [Fact]
    public void Constructor_ValidConnectionString_Succeeds()
    {
        const string connectionString = "Server=localhost;Database=test;";

        FluentActions.Invoking(() => new SqlServerSnapshotStore<TestState>(connectionString))
            .Should().NotThrow();
    }

    /// <inheritdoc/>
    protected override ISnapshotStore<TestState> CreateStore()
    {
        const string connectionString = "Server=localhost;Database=test;";
        return new SqlServerSnapshotStore<TestState>(connectionString);
    }
}
