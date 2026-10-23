using CourseWaitlist.Api.Data;
using CourseWaitlist.Api.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CourseWaitlist.Tests;

public sealed class ApplicationSetupTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public void Schema_is_created_at_startup()
    {
        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<CourseWaitlistDbContext>();

        Assert.Empty(context.Registrations);
    }

    [Fact]
    public void In_memory_database_survives_across_request_scopes()
    {
        var id = $"learner-{Guid.NewGuid():N}";

        using (var write = factory.Services.CreateScope())
        {
            var context = write.ServiceProvider.GetRequiredService<CourseWaitlistDbContext>();
            context.Learners.Add(new Learner { Id = id, PrerequisiteSatisfied = true });
            context.SaveChanges();
        }

        using var read = factory.Services.CreateScope();
        var readContext = read.ServiceProvider.GetRequiredService<CourseWaitlistDbContext>();
        Assert.Contains(readContext.Learners, l => l.Id == id);
    }

    [Fact]
    public void Host_shares_one_open_connection_across_scopes()
    {
        using var first = factory.Services.CreateScope();
        using var second = factory.Services.CreateScope();

        Assert.Same(
            first.ServiceProvider.GetRequiredService<SqliteConnection>(),
            second.ServiceProvider.GetRequiredService<SqliteConnection>());
    }
}
