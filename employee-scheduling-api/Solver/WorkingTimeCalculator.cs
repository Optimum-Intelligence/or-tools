using System.Globalization;
using EmployeeScheduling.Api.Models;

namespace EmployeeScheduling.Api.Solver;

/// <summary>
/// Work-period clock times ported from OptiFlight WorkingTimeCalculator.
/// Start rounds down to :00 or :30. End rounds up to the same grid, then
/// floors to the daily minimum. Dominant monthly start times are not modeled.
/// </summary>
public static class WorkingTimeCalculator
{
    public static DateTime RoundStart(DateTime earliest)
    {
        var minute = earliest.Minute < 30 ? 0 : 30;
        return new DateTime(earliest.Year, earliest.Month, earliest.Day, earliest.Hour, minute, 0, earliest.Kind);
    }

    public static DateTime ComputeEnd(DateTime startWorkingTime, DateTime latestEnd, int dailyMinHours)
    {
        DateTime end;
        if (startWorkingTime.Minute == 0)
        {
            end = latestEnd.Minute != 0
                ? new DateTime(latestEnd.Year, latestEnd.Month, latestEnd.Day, latestEnd.Hour, 0, 0, latestEnd.Kind).AddHours(1)
                : new DateTime(latestEnd.Year, latestEnd.Month, latestEnd.Day, latestEnd.Hour, latestEnd.Minute, 0, latestEnd.Kind);
        }
        else if (latestEnd.Minute < 30)
        {
            end = new DateTime(latestEnd.Year, latestEnd.Month, latestEnd.Day, latestEnd.Hour, 30, 0, latestEnd.Kind);
        }
        else
        {
            end = new DateTime(latestEnd.Year, latestEnd.Month, latestEnd.Day, latestEnd.Hour, 30, 0, latestEnd.Kind).AddHours(1);
        }

        var minEnd = startWorkingTime.AddHours(dailyMinHours);
        return end < minEnd ? minEnd : end;
    }

    public static int RoundedSpanMinutes(DateTime earliestStart, DateTime latestEnd, int dailyMinHours)
    {
        var start = RoundStart(earliestStart);
        var end = ComputeEnd(start, latestEnd, dailyMinHours);
        return MinutesBetween(start, end);
    }

    public static int MinutesBetween(DateTime earlier, DateTime later) =>
        (int)Math.Round((later - earlier).TotalMinutes, MidpointRounding.AwayFromZero);

    public static string Format(DateTime value) =>
        value.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>
    /// Calendar day a task counts toward. A task starting after midnight still
    /// belongs to the previous day when it starts before that day's rounded
    /// start plus the daily max.
    /// </summary>
    public static DateOnly GetWorkPeriodReferenceDate(
        int dailyMaxHours,
        TaskDefinition task,
        IReadOnlyList<TaskDefinition> employeeTasks)
    {
        var shiftDate = DateOnly.FromDateTime(task.Start);
        var previousDay = shiftDate.AddDays(-1);
        var previousDayTasks = employeeTasks
            .Where(other => DateOnly.FromDateTime(other.Start) == previousDay)
            .ToList();
        if (previousDayTasks.Count == 0)
        {
            return shiftDate;
        }

        var effectiveStart = RoundStart(previousDayTasks.Min(other => other.Start));
        var cutoff = effectiveStart.AddHours(dailyMaxHours);
        if (DateOnly.FromDateTime(cutoff) > previousDay && task.Start < cutoff)
        {
            return previousDay;
        }

        return shiftDate;
    }

    public static IReadOnlyList<WorkPeriodGroup> Group(int dailyMaxHours, IReadOnlyList<TaskDefinition> tasks)
    {
        if (tasks.Count == 0)
        {
            return Array.Empty<WorkPeriodGroup>();
        }

        return tasks
            .GroupBy(task => GetWorkPeriodReferenceDate(dailyMaxHours, task, tasks))
            .OrderBy(group => group.Key)
            .Select(group => new WorkPeriodGroup(
                group.Key,
                group.OrderBy(task => task.Start).ThenBy(task => task.Id, StringComparer.Ordinal).ToList()))
            .ToList();
    }
}

public sealed record WorkPeriodGroup(DateOnly Date, IReadOnlyList<TaskDefinition> Tasks);
