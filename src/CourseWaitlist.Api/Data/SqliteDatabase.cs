using Microsoft.Data.Sqlite;

namespace CourseWaitlist.Api.Data;

public static class SqliteDatabase
{
    // A private in-memory database lives only as long as its open connection, so each connection is its own isolated database.
    public const string InMemoryConnectionString = "Data Source=:memory:";

    public static SqliteConnection CreateOpenConnection(string connectionString)
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    public static void EnsureSchema(CourseWaitlistDbContext context) => context.Database.EnsureCreated();
}
