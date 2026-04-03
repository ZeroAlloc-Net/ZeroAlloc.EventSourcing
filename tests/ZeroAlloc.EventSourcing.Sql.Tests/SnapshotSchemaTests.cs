using FluentAssertions;
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
}
