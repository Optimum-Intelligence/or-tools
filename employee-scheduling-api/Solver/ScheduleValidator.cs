using System.Globalization;
using EmployeeScheduling.Api.Models;

namespace EmployeeScheduling.Api.Solver;

public static class ScheduleValidator
{
    public static List<string> Validate(ScheduleRequest request)
    {
        var errors = new List<string>();
        var employees = request.Employees ?? new();
        var flights = request.Flights ?? new();
        var fixedAssignments = request.FixedAssignments ?? new();

        if (request.TimeLimitSeconds <= 0)
        {
            errors.Add("timeLimitSeconds must be greater than zero.");
        }

        if (request.MinimumMinutesBetweenTwoTasks < 0)
        {
            errors.Add("minimumMinutesBetweenTwoTasks must be zero or greater.");
        }

        if (request.MinimumHoursBetweenWorkPeriods < 0)
        {
            errors.Add("minimumHoursBetweenWorkPeriods must be zero or greater.");
        }

        if (request.MinimizeEmployeesWeight < 0)
        {
            errors.Add("minimizeEmployeesWeight must be zero or greater.");
        }

        if (request.IdleTimeWeight < 0)
        {
            errors.Add("idleTimeWeight must be zero or greater.");
        }

        if (employees.Count == 0)
        {
            errors.Add("employees must contain at least one employee.");
        }

        var employeesByName = new Dictionary<string, EmployeeDefinition>(StringComparer.Ordinal);
        foreach (var employee in employees)
        {
            if (string.IsNullOrWhiteSpace(employee.Name))
            {
                errors.Add("Every employee needs a name.");
                continue;
            }

            if (!employeesByName.TryAdd(employee.Name, employee))
            {
                errors.Add($"Duplicate employee name '{employee.Name}'.");
            }

            if (employee.Skills is null || employee.Skills.Count == 0 || employee.Skills.Any(string.IsNullOrWhiteSpace))
            {
                errors.Add($"Employee '{employee.Name}' needs at least one skill.");
            }

            if (employee.DailyMinWorkingHour < 0 || employee.DailyMaxWorkingHour < employee.DailyMinWorkingHour)
            {
                errors.Add($"Employee '{employee.Name}' has an invalid daily min/max.");
            }

            if (employee.DailyMaxWorkingHour < 1)
            {
                errors.Add($"Employee '{employee.Name}' dailyMaxWorkingHour must be at least 1.");
            }

            var seenDays = new HashSet<string>(StringComparer.Ordinal);
            foreach (var day in employee.Unavailable ?? new())
            {
                if (!DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                {
                    errors.Add($"Employee '{employee.Name}' unavailable date '{day}' must be yyyy-MM-dd.");
                }
                else if (!seenDays.Add(day))
                {
                    errors.Add($"Employee '{employee.Name}' lists unavailable date '{day}' more than once.");
                }
            }
        }

        if (flights.Count == 0)
        {
            errors.Add("flights must contain at least one flight.");
        }

        var flightIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var flight in flights)
        {
            if (string.IsNullOrWhiteSpace(flight.Id))
            {
                errors.Add("Every flight needs an id.");
                continue;
            }

            if (!flightIds.Add(flight.Id))
            {
                errors.Add($"Duplicate flight id '{flight.Id}'.");
            }

            if (flight.End <= flight.Start)
            {
                errors.Add($"Flight '{flight.Id}' end must be after start.");
            }

            if (flight.RequiredEmployees is null || flight.RequiredEmployees.Count == 0)
            {
                errors.Add($"Flight '{flight.Id}' needs at least one requiredEmployees group.");
                continue;
            }

            foreach (var requirement in flight.RequiredEmployees)
            {
                if (requirement.Skills is null || requirement.Skills.Count == 0 || requirement.Skills.Any(string.IsNullOrWhiteSpace))
                {
                    errors.Add($"Flight '{flight.Id}' has a requiredEmployees group with no skills.");
                }

                if (requirement.NumberOfEmployees < 1)
                {
                    errors.Add($"Flight '{flight.Id}' numberOfEmployees must be at least 1.");
                }
            }
        }

        if (errors.Count > 0)
        {
            return errors;
        }

        var tasks = TaskExpander.Expand(flights);
        foreach (var flight in flights)
        {
            var slots = tasks.Where(task => task.FlightId == flight.Id).ToList();
            var candidates = new HashSet<string>(StringComparer.Ordinal);
            foreach (var slot in slots)
            {
                var qualified = employees.Where(employee => CanWork(employee, slot)).Select(employee => employee.Name).ToList();
                if (qualified.Count == 0)
                {
                    errors.Add($"Task '{slot.Id}' has no qualified available employee who can fit it in their daily max.");
                }

                foreach (var name in qualified)
                {
                    candidates.Add(name);
                }
            }

            if (candidates.Count < slots.Count)
            {
                errors.Add(
                    $"Flight '{flight.Id}' needs {slots.Count} people at the same time but only {candidates.Count} qualify.");
            }
        }

        var pinnedTasks = new HashSet<string>(StringComparer.Ordinal);
        var pinsByEmployee = new Dictionary<string, List<TaskDefinition>>(StringComparer.Ordinal);
        var tasksById = tasks.ToDictionary(task => task.Id, StringComparer.Ordinal);
        foreach (var pin in fixedAssignments)
        {
            if (!employeesByName.TryGetValue(pin.Employee, out var employee))
            {
                errors.Add($"Fixed assignment references unknown employee '{pin.Employee}'.");
                continue;
            }

            if (!tasksById.TryGetValue(pin.TaskId, out var task))
            {
                errors.Add(
                    $"Fixed assignment for '{pin.Employee}' references unknown task '{pin.TaskId}'. Task ids look like {{flightId}}${{group}}_{{slot}}.");
                continue;
            }

            if (!pinnedTasks.Add(pin.TaskId))
            {
                errors.Add($"Task '{pin.TaskId}' is fixed to more than one employee.");
            }

            if (!CanWork(employee, task))
            {
                errors.Add($"Employee '{pin.Employee}' cannot work task '{pin.TaskId}'.");
            }

            if (!pinsByEmployee.TryGetValue(pin.Employee, out var pinned))
            {
                pinned = new List<TaskDefinition>();
                pinsByEmployee[pin.Employee] = pinned;
            }

            pinned.Add(task);
        }

        foreach (var (name, pinned) in pinsByEmployee)
        {
            var employee = employeesByName[name];
            for (var i = 0; i < pinned.Count; i++)
            {
                for (var j = i + 1; j < pinned.Count; j++)
                {
                    if (TaskConstraints.Conflicts(
                            pinned[i],
                            pinned[j],
                            employee.DailyMinWorkingHour,
                            employee.DailyMaxWorkingHour,
                            request.MinimumMinutesBetweenTwoTasks,
                            request.MinimumHoursBetweenWorkPeriods))
                    {
                        errors.Add(
                            $"Fixed assignments for '{name}' on '{pinned[i].Id}' and '{pinned[j].Id}' conflict (overlap, gap, daily max, or rest).");
                    }
                }
            }
        }

        return errors;
    }

    public static bool CanWork(EmployeeDefinition employee, TaskDefinition task)
    {
        if (!IsAvailable(employee, task.Start))
        {
            return false;
        }

        if (!HasSkills(employee, task.RequiredSkills))
        {
            return false;
        }

        var span = WorkingTimeCalculator.RoundedSpanMinutes(
            task.Start,
            task.End,
            employee.DailyMinWorkingHour);
        return span <= employee.DailyMaxWorkingHour * 60;
    }

    public static bool HasSkills(EmployeeDefinition employee, IReadOnlyList<string> required)
    {
        if (required is null || required.Count == 0)
        {
            return false;
        }

        var skills = new HashSet<string>(employee.Skills ?? new(), StringComparer.Ordinal);
        return required.All(skills.Contains);
    }

    public static bool IsAvailable(EmployeeDefinition employee, DateTime start)
    {
        var day = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return !(employee.Unavailable ?? new()).Contains(day, StringComparer.Ordinal);
    }
}
