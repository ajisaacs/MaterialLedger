using MaterialLedger.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MaterialLedger.Tests;

public class SqlServerTestDatabaseFixture : IAsyncLifetime
{
    private const string TestDatabaseName = "MaterialLedgerTest";
    private string _adminConnectionString = string.Empty;

    public MaterialLedgerDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MaterialLedgerDbContext>()
            .UseSqlServer(ConnectionStringForDatabase(TestDatabaseName))
            .Options;
        return new MaterialLedgerDbContext(options);
    }

    public async Task InitializeAsync()
    {
        _adminConnectionString = Environment.GetEnvironmentVariable("MATERIALLEDGER_TEST_ADMIN_CONNECTION")
            ?? throw new InvalidOperationException(
                "Set MATERIALLEDGER_TEST_ADMIN_CONNECTION (an sa-level connection string to forge-sql, " +
                "e.g. Server=10.10.100.134,1433;User Id=sa;Password=...;TrustServerCertificate=True;) " +
                "before running schema/idempotency tests. These tests provision and drop a real " +
                "MaterialLedgerTest database — they are deliberately not silently skipped.");

        await using var connection = new SqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using (var drop = connection.CreateCommand())
        {
            drop.CommandText =
                $"IF DB_ID('{TestDatabaseName}') IS NOT NULL " +
                $"BEGIN ALTER DATABASE [{TestDatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{TestDatabaseName}]; END";
            await drop.ExecuteNonQueryAsync();
        }
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = $"CREATE DATABASE [{TestDatabaseName}]";
            await create.ExecuteNonQueryAsync();
        }

        await using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await using var connection = new SqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using var drop = connection.CreateCommand();
        drop.CommandText =
            $"IF DB_ID('{TestDatabaseName}') IS NOT NULL " +
            $"BEGIN ALTER DATABASE [{TestDatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{TestDatabaseName}]; END";
        await drop.ExecuteNonQueryAsync();
    }

    private string ConnectionStringForDatabase(string databaseName)
    {
        var builder = new SqlConnectionStringBuilder(_adminConnectionString) { InitialCatalog = databaseName };
        return builder.ConnectionString;
    }
}
