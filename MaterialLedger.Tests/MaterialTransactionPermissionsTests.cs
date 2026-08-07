using Microsoft.Data.SqlClient;
using Xunit;

namespace MaterialLedger.Tests;

/// <summary>
/// Verifies the append-only DENY on material.MaterialTransactions (see
/// docs/schema/material-permissions-v1.sql, applied live via Task 5) is actually enforced by SQL
/// Server for the materialledger_app login — not just documented in a SQL script that could drift
/// from live database state.
///
/// Unlike the rest of the suite, this test intentionally does NOT use
/// <see cref="SqlServerTestDatabaseFixture"/>'s throwaway MaterialLedgerTest database — permission
/// grants live on the real MaterialLedger database's principals, so this connects directly to it
/// as materialledger_app. It targets a MaterialTransactionId that cannot exist (-1) so that even in
/// the failure case (the DENY not being in effect), the UPDATE matches zero rows and cannot mutate
/// real data. This test only ever reads/attempts a no-op write; it never creates or drops anything.
/// </summary>
[Trait("Category", "Integration")]
public class MaterialTransactionPermissionsTests
{
    [Fact]
    public async Task MaterialledgerApp_CannotUpdateMaterialTransactions()
    {
        var appConnectionString = Environment.GetEnvironmentVariable("MATERIALLEDGER_TEST_APP_CONNECTION")
            ?? throw new InvalidOperationException(
                "Set MATERIALLEDGER_TEST_APP_CONNECTION (a materialledger_app-level connection " +
                "string to forge-sql's REAL MaterialLedger database, e.g. Server=10.10.100.134,1433;" +
                "Database=MaterialLedger;User Id=materialledger_app;Password=...;" +
                "TrustServerCertificate=True;) before running permission-enforcement tests. This " +
                "test proves the append-only DENY UPDATE/DELETE on material.MaterialTransactions " +
                "(docs/schema/material-permissions-v1.sql) is actually enforced by the live " +
                "database, not just documented. It targets a nonexistent row and does not create " +
                "or drop anything, so it is safe to point at production. It is deliberately not " +
                "silently skipped when the connection string is missing.");

        await using var connection = new SqlConnection(appConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE material.MaterialTransactions SET Notes = 'permission-test' WHERE MaterialTransactionId = -1";

        var ex = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
        Assert.Contains("UPDATE permission", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
