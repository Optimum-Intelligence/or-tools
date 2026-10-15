namespace EmployeeScheduling.Api.Models;

public sealed record ScheduleRequest
{
    public double TimeLimitSeconds { get; init; } = 10;
    public int MinimumMinutesBetweenTwoTasks { get; init; }
    public int MinimumHoursBetweenWorkPeriods { get; init; } = 11;
    public int MinimizeEmployeesWeight { get; init; } = 1;
    public int IdleTimeWeight { get; init; } = 1;
    public List<EmployeeDefinition> Employees { get; init; } = new();
    public List<FlightDefinition> Flights { get; init; } = new();
    public List<FixedAssignment> FixedAssignments { get; init; } = new();
}

public sealed record EmployeeDefinition
{
    public string Name { get; init; } = "";
    public List<string> Skills { get; init; } = new();
    public List<string> Unavailable { get; init; } = new();
    public int DailyMinWorkingHour { get; init; }
    public int DailyMaxWorkingHour { get; init; }
}

public sealed record FlightDefinition
{
    public string Id { get; init; } = "";
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public List<RequiredEmployeeGroup> RequiredEmployees { get; init; } = new();
}

public sealed record RequiredEmployeeGroup
{
    public List<string> Skills { get; init; } = new();
    public int NumberOfEmployees { get; init; }
}

public sealed record FixedAssignment
{
    public string Employee { get; init; } = "";
    public string TaskId { get; init; } = "";
}

/// <summary>
/// One staffing slot expanded from a flight, same idea as OptiFlight's Shift.
/// Id shape: {flightId}${group}_{slot}.
/// </summary>
public sealed record TaskDefinition
{
    public string Id { get; init; } = "";
    public string FlightId { get; init; } = "";
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public List<string> RequiredSkills { get; init; } = new();
}
