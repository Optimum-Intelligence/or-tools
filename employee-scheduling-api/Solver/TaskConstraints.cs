using EmployeeScheduling.Api.Models;

namespace EmployeeScheduling.Api.Solver;

/// <summary>
/// Pairwise hard rules for two tasks on one employee. Same calendar day is one
/// work period. A later-day task is in that period when it starts before the
/// earlier task's rounded start plus the daily max (overnight carryover).
/// </summary>
public static class TaskConstraints
{
    public static bool Conflicts(
        TaskDefinition left,
        TaskDefinition right,
        int dailyMinWorkingHour,
        int dailyMaxWorkingHour,
        int minimumMinutesBetweenTwoTasks,
        int minimumHoursBetweenWorkPeriods)
    {
        var earlier = left.Start <= right.Start ? left : right;
        var later = left.Start <= right.Start ? right : left;
        if (Overlaps(earlier, later))
        {
            return true;
        }

        var gap = WorkingTimeCalculator.MinutesBetween(earlier.End, later.Start);
        if (gap < minimumMinutesBetweenTwoTasks)
        {
            return true;
        }

        if (SameWorkPeriod(earlier, later, dailyMaxWorkingHour))
        {
            var latestEnd = earlier.End >= later.End ? earlier.End : later.End;
            var span = WorkingTimeCalculator.RoundedSpanMinutes(earlier.Start, latestEnd, dailyMinWorkingHour);
            return span > dailyMaxWorkingHour * 60;
        }

        var rest = WorkingTimeCalculator.MinutesBetween(earlier.End, later.Start);
        return rest < minimumHoursBetweenWorkPeriods * 60;
    }

    public static bool Overlaps(TaskDefinition earlier, TaskDefinition later) =>
        earlier.Start < later.End && later.Start < earlier.End;

    public static bool SameWorkPeriod(TaskDefinition earlier, TaskDefinition later, int dailyMaxHours)
    {
        if (DateOnly.FromDateTime(earlier.Start) == DateOnly.FromDateTime(later.Start))
        {
            return true;
        }

        var cutoff = WorkingTimeCalculator.RoundStart(earlier.Start).AddHours(dailyMaxHours);
        return later.Start < cutoff;
    }
}
