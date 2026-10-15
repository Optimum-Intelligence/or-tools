using EmployeeScheduling.Api.Models;
using EmployeeScheduling.Api.Solver;
using EmployeeScheduling.Api.Swagger;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);
if (Environment.GetEnvironmentVariable("PORT") is { Length: > 0 } port)
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}

builder.Services.AddSingleton<EmployeeSchedulingSolver>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Employee scheduling",
        Version = "v1",
        Description = "Assigns employees to timed flight tasks. One work period (shift) can cover multiple tasks. Task ids are {flightId}${group}_{slot}.",
    });
    options.OperationFilter<SampleRequestOperationFilter>();
});

var app = builder.Build();
app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "Employee scheduling");
    options.DocumentTitle = "Employee scheduling";
});

app.MapGet("/", () => Results.Redirect("/swagger"));

app.MapPost("/schedule", (ScheduleRequest? request, EmployeeSchedulingSolver solver) =>
{
    if (request is null)
    {
        return Results.BadRequest(new ErrorResponse(["Request body is required."]));
    }

    var errors = ScheduleValidator.Validate(request);
    if (errors.Count > 0)
    {
        return Results.BadRequest(new ErrorResponse(errors));
    }

    return Results.Ok(solver.Solve(request));
})
.WithName("ScheduleEmployees")
.WithSummary("Solve a multi-task employee schedule")
.WithDescription("Accepts employees and flights. Each flight expands into staffing tasks. An employee work period can cover several non-overlapping tasks. Returns work periods, flight coverage, and penalties.")
.Produces<ScheduleResponse>(StatusCodes.Status200OK)
.Produces<ErrorResponse>(StatusCodes.Status400BadRequest);

app.Run();
