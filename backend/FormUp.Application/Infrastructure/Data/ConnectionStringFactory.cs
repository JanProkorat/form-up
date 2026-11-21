using Npgsql;

namespace FormUp.Application.Infrastructure.Data;

/// <summary>
/// Factory for building connection strings.
/// </summary>
public static class ConnectionStringFactory
{
    /// <summary>
    /// Builds a connection string for PostgreSQL.
    /// </summary>
    /// <param name="config"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public static string BuildPostgres(IConfiguration config)
    {
        var conn = config.GetConnectionString("PostgreSQl")
                   ?? throw new InvalidOperationException("Connection string not found");

        var password = config["POSTGRES_PASSWORD"]
                       ?? throw new InvalidOperationException("POSTGRES_PASSWORD not set");

        return conn + $";Password={password}";
    }

    /// <summary>
    /// Describes a PostgreSQL connection string for logs using an allow-list (host, port, database, user).
    /// Returns a fixed placeholder when the string cannot be parsed; never echoes the input.
    /// </summary>
    /// <param name="connectionString">Full connection string, possibly containing secrets.</param>
    public static string DescribePostgres(string connectionString)
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);

            return $"Host={builder.Host};Port={builder.Port};Database={builder.Database};Username={builder.Username}";
        }
        catch (ArgumentException)
        {
            return "<unparseable connection string>";
        }
        catch (FormatException)
        {
            return "<unparseable connection string>";
        }
    }

    /// <summary>
    /// Builds a connection string for MongoDB.
    /// </summary>
    /// <param name="config"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public static string BuildMongo(IConfiguration config)
    {
        var conn = config.GetConnectionString("MongoDB")
                   ?? throw new InvalidOperationException("MongoDB connection string not found");

        var password = config["MONGO_PASSWORD"]
                       ?? throw new InvalidOperationException("MONGO_PASSWORD not set");

        return conn.Contains("{0}") ? string.Format(conn, password) : conn;
    }
}