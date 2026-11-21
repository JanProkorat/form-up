using FormUp.Application.Infrastructure.Data;
using FluentAssertions;

namespace FormUp.Tests.Infrastructure.Data;

/// <summary>
/// Unit tests for <see cref="ConnectionStringFactory.DescribePostgres"/> — the log-safe
/// description of a PostgreSQL connection string.
/// </summary>
public class ConnectionStringFactoryTests
{
    private const string FakePassword = "fake-password-not-real";
    private const string FakeSslPassword = "fake-ssl-key-password-not-real";

    [Fact]
    public void DescribePostgres_ConnectionStringWithPassword_DoesNotContainPassword()
    {
        var connectionString = $"Host=db.example.test;Port=5432;Database=formup;Username=app_user;Password={FakePassword}";

        var result = ConnectionStringFactory.DescribePostgres(connectionString);

        result.Should().NotContain(FakePassword);
        result.Should().NotContainEquivalentOf("Password");
    }

    [Fact]
    public void DescribePostgres_ConnectionStringWithSslPassword_DoesNotContainSslPassword()
    {
        var connectionString =
            $"Host=db.example.test;Database=formup;Username=app_user;Password={FakePassword};SSL Password={FakeSslPassword}";

        var result = ConnectionStringFactory.DescribePostgres(connectionString);

        result.Should().NotContain(FakeSslPassword);
        result.Should().NotContain(FakePassword);
    }

    [Fact]
    public void DescribePostgres_ValidConnectionString_ContainsHostPortDatabaseAndUsername()
    {
        var connectionString = $"Host=db.example.test;Port=6543;Database=formup;Username=app_user;Password={FakePassword}";

        var result = ConnectionStringFactory.DescribePostgres(connectionString);

        result.Should().Contain("Host=db.example.test");
        result.Should().Contain("Port=6543");
        result.Should().Contain("Database=formup");
        result.Should().Contain("Username=app_user");
    }

    [Fact]
    public void DescribePostgres_UnparseableConnectionString_DoesNotEchoInput()
    {
        var connectionString = $"NotAKnownKeyword=abc;Password={FakePassword}";

        var result = ConnectionStringFactory.DescribePostgres(connectionString);

        result.Should().NotContain(FakePassword);
        result.Should().NotContain("NotAKnownKeyword");
    }
}
