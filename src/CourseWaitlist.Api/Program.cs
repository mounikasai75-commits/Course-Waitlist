using CourseWaitlist.Api.Data;
using CourseWaitlist.Api.Endpoints;
using CourseWaitlist.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
    options.OperationFilter<LearnerRequestBodyOperationFilter>());

var connectionString = builder.Configuration.GetConnectionString("CourseWaitlist")
    ?? SqliteDatabase.InMemoryConnectionString;

// One open connection for the whole host keeps the in-memory database alive across requests; the container disposes it on shutdown.
builder.Services.AddSingleton(_ => SqliteDatabase.CreateOpenConnection(connectionString));

builder.Services.AddDbContext<CourseWaitlistDbContext>((serviceProvider, options) =>
    options.UseSqlite(serviceProvider.GetRequiredService<SqliteConnection>()));

// Serializes use of the shared connection and the DbContexts built on it.
builder.Services.AddSingleton(new SemaphoreSlim(1, 1));

builder.Services.AddScoped<IRegistrationService, RegistrationService>();

builder.Services.AddSingleton(builder.Configuration.GetSection("Course").Get<CourseSettings>() ?? new CourseSettings());

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<CourseWaitlistDbContext>();
    SqliteDatabase.EnsureSchema(context);
    CourseSeeder.Seed(context, app.Services.GetRequiredService<CourseSettings>());
}

app.UseApiExceptionHandler();
app.MapCourseEndpoints();

app.Run();

public partial class Program;
