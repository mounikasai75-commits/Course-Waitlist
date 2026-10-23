using CourseWaitlist.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CourseWaitlist.Tests;

// Each instance owns its own open ":memory:" connection, i.e. its own isolated database.
public sealed class TestDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<CourseWaitlistDbContext> _options;

    public TestDatabase()
    {
        _connection = SqliteDatabase.CreateOpenConnection(SqliteDatabase.InMemoryConnectionString);
        _options = new DbContextOptionsBuilder<CourseWaitlistDbContext>().UseSqlite(_connection).Options;

        using var context = CreateContext();
        SqliteDatabase.EnsureSchema(context);
    }

    // Callers get a new context per unit of work; contexts are never shared between operations.
    public CourseWaitlistDbContext CreateContext() => new(_options);

    // Lets a test inject a failure at a chosen point of SaveChanges to verify rollback.
    public CourseWaitlistDbContext CreateContext(IInterceptor interceptor) =>
        new(new DbContextOptionsBuilder<CourseWaitlistDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(interceptor)
            .Options);

    public void Dispose() => _connection.Dispose();
}
