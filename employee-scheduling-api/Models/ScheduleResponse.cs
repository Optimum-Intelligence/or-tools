namespace EmployeeScheduling.Api.Models;

public sealed record ScheduleResponse(
    string Status,
    double WallTimeSeconds,
    double? Objective,
    IReadOnlyList<EmployeeSchedule> Schedule,
    IReadOnlyList<FlightCoverage> Flights,
    IReadOnlyList<PenaltyRow> Penalties);

public sealed record EmployeeSchedule(
    string Employee,
    IReadOnlyList<WorkPeriod> WorkPeriods);

public sealed record WorkPeriod(
    string Date,
    string StartWorkingTime,
    string EndWorkingTime,
    int UsefulMinutes,
    int TotalMinutes,
    IReadOnlyList<AssignedTask> AssignedTasks);

public sealed record AssignedTask(
    string Id,
    string FlightId,
    IReadOnlyList<string> Skills,
    string Start,
    string End);

public sealed record FlightCoverage(
    string FlightId,
    string Start,
    string End,
    IReadOnlyList<SlotCoverage> Slots);

public sealed record SlotCoverage(
    string TaskId,
    IReadOnlyList<string> Skills,
    string? Employee);

public sealed record PenaltyRow(string Name, long Value, int Weight, long Cost);
