using System.Globalization;
using EmployeeScheduling.Api.Models;
using Google.OrTools.Sat;

namespace EmployeeScheduling.Api.Solver;

public sealed class EmployeeSchedulingSolver
{
    public ScheduleResponse Solve(ScheduleRequest request)
    {
        var tasks = TaskExpander.Expand(request.Flights);
        var model = new CpModel();
        var assigned = new Dictionary<(string Employee, string Task), BoolVar>();
        var penalties = new List<(string Name, IntVar Variable, int Weight)>();

        foreach (var employee in request.Employees)
        {
            foreach (var task in tasks)
            {
                if (!ScheduleValidator.CanWork(employee, task))
                {
                    continue;
                }

                assigned[(employee.Name, task.Id)] = model.NewBoolVar($"{Safe(employee.Name)}_{Safe(task.Id)}");
            }
        }

        foreach (var task in tasks)
        {
            var qualified = request.Employees
                .Select(employee => assigned.GetValueOrDefault((employee.Name, task.Id)))
                .Where(variable => variable is not null)
                .Cast<BoolVar>()
                .ToList();
            model.Add(LinearExpr.Sum(qualified) == 1);
        }

        foreach (var pin in request.FixedAssignments ?? new())
        {
            model.Add(assigned[(pin.Employee, pin.TaskId)] == 1);
        }

        foreach (var employee in request.Employees)
        {
            var eligible = tasks
                .Where(task => assigned.ContainsKey((employee.Name, task.Id)))
                .ToList();
            AddPairwise(model, employee, eligible, assigned, request);
            AddDailyWindows(model, employee, eligible, assigned, request, penalties);
        }

        if (penalties.Count == 0)
        {
            model.Minimize(LinearExpr.Constant(0));
        }
        else
        {
            model.Minimize(LinearExpr.WeightedSum(
                penalties.Select(penalty => (LinearExpr)penalty.Variable),
                penalties.Select(penalty => (long)penalty.Weight)));
        }

        using var solver = new CpSolver();
        solver.StringParameters = string.Create(
            CultureInfo.InvariantCulture,
            $"max_time_in_seconds:{request.TimeLimitSeconds}");
        var status = solver.Solve(model);
        var solved = status is CpSolverStatus.Optimal or CpSolverStatus.Feasible;
        if (!solved)
        {
            return new ScheduleResponse(
                StatusName(status),
                solver.WallTime(),
                null,
                Array.Empty<EmployeeSchedule>(),
                Array.Empty<FlightCoverage>(),
                Array.Empty<PenaltyRow>());
        }

        var schedule = request.Employees
            .Select(employee => BuildEmployeeSchedule(employee, tasks, assigned, solver))
            .ToList();
        var flights = request.Flights
            .Select(flight => BuildFlightCoverage(flight, tasks, request.Employees, assigned, solver))
            .ToList();
        var penaltyRows = penalties
            .Select(penalty =>
            {
                var value = solver.Value(penalty.Variable);
                return new PenaltyRow(penalty.Name, value, penalty.Weight, value * penalty.Weight);
            })
            .Where(row => row.Value != 0 && row.Weight != 0)
            .ToList();

        return new ScheduleResponse(
            StatusName(status),
            solver.WallTime(),
            solver.ObjectiveValue,
            schedule,
            flights,
            penaltyRows);
    }

    private static void AddPairwise(
        CpModel model,
        EmployeeDefinition employee,
        IReadOnlyList<TaskDefinition> eligible,
        Dictionary<(string Employee, string Task), BoolVar> assigned,
        ScheduleRequest request)
    {
        for (var i = 0; i < eligible.Count; i++)
        {
            for (var j = i + 1; j < eligible.Count; j++)
            {
                if (!TaskConstraints.Conflicts(
                        eligible[i],
                        eligible[j],
                        employee.DailyMinWorkingHour,
                        employee.DailyMaxWorkingHour,
                        request.MinimumMinutesBetweenTwoTasks,
                        request.MinimumHoursBetweenWorkPeriods))
                {
                    continue;
                }

                model.AddAtMostOne(new ILiteral[]
                {
                    assigned[(employee.Name, eligible[i].Id)],
                    assigned[(employee.Name, eligible[j].Id)],
                });
            }
        }
    }

    private static void AddDailyWindows(
        CpModel model,
        EmployeeDefinition employee,
        IReadOnlyList<TaskDefinition> eligible,
        Dictionary<(string Employee, string Task), BoolVar> assigned,
        ScheduleRequest request,
        List<(string Name, IntVar Variable, int Weight)> penalties)
    {
        foreach (var group in eligible.GroupBy(task => DateOnly.FromDateTime(task.Start)))
        {
            var dayTasks = group.ToList();
            var dayVars = dayTasks
                .Select(task => assigned[(employee.Name, task.Id)])
                .ToList();
            var dayLabel = group.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var works = model.NewBoolVar($"{Safe(employee.Name)}_works_{dayLabel}");
            model.Add(LinearExpr.Sum(dayVars) >= 1).OnlyEnforceIf(works);
            model.Add(LinearExpr.Sum(dayVars) == 0).OnlyEnforceIf(works.Not());

            if (request.MinimizeEmployeesWeight != 0)
            {
                penalties.Add((
                    $"{employee.Name} works {dayLabel}",
                    works,
                    request.MinimizeEmployeesWeight));
            }

            var minStart = dayTasks.Min(task => ToMinutes(task.Start));
            var maxEnd = dayTasks.Max(task => ToMinutes(task.End));
            var big = maxEnd + 1;
            var startCandidates = new List<IntVar>(dayTasks.Count);
            var endCandidates = new List<IntVar>(dayTasks.Count);
            foreach (var task in dayTasks)
            {
                var assign = assigned[(employee.Name, task.Id)];
                var token = $"{Safe(employee.Name)}_{Safe(task.Id)}";
                var startCandidate = model.NewIntVar(minStart, big, $"{token}_start");
                model.Add(startCandidate == ToMinutes(task.Start)).OnlyEnforceIf(assign);
                model.Add(startCandidate == big).OnlyEnforceIf(assign.Not());
                startCandidates.Add(startCandidate);

                var endCandidate = model.NewIntVar(0, maxEnd, $"{token}_end");
                model.Add(endCandidate == ToMinutes(task.End)).OnlyEnforceIf(assign);
                model.Add(endCandidate == 0).OnlyEnforceIf(assign.Not());
                endCandidates.Add(endCandidate);
            }

            var startVar = model.NewIntVar(minStart, big, $"{Safe(employee.Name)}_{dayLabel}_start");
            var endVar = model.NewIntVar(0, maxEnd, $"{Safe(employee.Name)}_{dayLabel}_end");
            model.AddMinEquality(startVar, startCandidates);
            model.AddMaxEquality(endVar, endCandidates);

            var maxRaw = Math.Max(0, maxEnd - minStart);
            var dailyMax = employee.DailyMaxWorkingHour * 60;
            var dailyMin = employee.DailyMinWorkingHour * 60;
            var upper = Math.Max(maxRaw, Math.Max(dailyMin, dailyMax));
            var span = model.NewIntVar(0, Math.Max(maxRaw, 0), $"{Safe(employee.Name)}_{dayLabel}_span");
            model.Add(span == endVar - startVar).OnlyEnforceIf(works);
            model.Add(span == 0).OnlyEnforceIf(works.Not());

            var useful = model.NewIntVar(0, Math.Max(maxRaw, 0), $"{Safe(employee.Name)}_{dayLabel}_useful");
            model.Add(useful == LinearExpr.WeightedSum(
                dayTasks.Select(task => (LinearExpr)assigned[(employee.Name, task.Id)]),
                dayTasks.Select(task => (long)WorkingTimeCalculator.MinutesBetween(task.Start, task.End))));

            var window = model.NewIntVar(0, upper, $"{Safe(employee.Name)}_{dayLabel}_window");
            model.Add(window >= span);
            model.Add(window >= dailyMin).OnlyEnforceIf(works);
            model.Add(window == 0).OnlyEnforceIf(works.Not());
            model.Add(window <= dailyMax).OnlyEnforceIf(works);

            if (request.IdleTimeWeight != 0)
            {
                var idle = model.NewIntVar(0, upper, $"{Safe(employee.Name)}_{dayLabel}_idle");
                model.Add(idle == window - useful).OnlyEnforceIf(works);
                model.Add(idle == 0).OnlyEnforceIf(works.Not());
                penalties.Add((
                    $"{employee.Name} idle minutes on {dayLabel}",
                    idle,
                    request.IdleTimeWeight));
            }
        }
    }

    private static EmployeeSchedule BuildEmployeeSchedule(
        EmployeeDefinition employee,
        IReadOnlyList<TaskDefinition> tasks,
        Dictionary<(string Employee, string Task), BoolVar> assigned,
        CpSolver solver)
    {
        var taken = tasks
            .Where(task => assigned.TryGetValue((employee.Name, task.Id), out var variable)
                && solver.Value(variable) == 1)
            .ToList();
        var periods = WorkingTimeCalculator.Group(employee.DailyMaxWorkingHour, taken)
            .Select(group =>
            {
                var earliest = group.Tasks.Min(task => task.Start);
                var latest = group.Tasks.Max(task => task.End);
                var start = WorkingTimeCalculator.RoundStart(earliest);
                var end = WorkingTimeCalculator.ComputeEnd(start, latest, employee.DailyMinWorkingHour);
                var useful = group.Tasks.Sum(task => WorkingTimeCalculator.MinutesBetween(task.Start, task.End));
                var assignedTasks = group.Tasks
                    .Select(task => new AssignedTask(
                        task.Id,
                        task.FlightId,
                        task.RequiredSkills,
                        WorkingTimeCalculator.Format(task.Start),
                        WorkingTimeCalculator.Format(task.End)))
                    .ToList();
                return new WorkPeriod(
                    group.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    WorkingTimeCalculator.Format(start),
                    WorkingTimeCalculator.Format(end),
                    useful,
                    WorkingTimeCalculator.MinutesBetween(start, end),
                    assignedTasks);
            })
            .ToList();
        return new EmployeeSchedule(employee.Name, periods);
    }

    private static FlightCoverage BuildFlightCoverage(
        FlightDefinition flight,
        IReadOnlyList<TaskDefinition> tasks,
        IReadOnlyList<EmployeeDefinition> employees,
        Dictionary<(string Employee, string Task), BoolVar> assigned,
        CpSolver solver)
    {
        var slots = tasks
            .Where(task => task.FlightId == flight.Id)
            .Select(task =>
            {
                var owner = employees.FirstOrDefault(employee =>
                    assigned.TryGetValue((employee.Name, task.Id), out var variable)
                    && solver.Value(variable) == 1);
                return new SlotCoverage(task.Id, task.RequiredSkills, owner?.Name);
            })
            .ToList();
        return new FlightCoverage(
            flight.Id,
            WorkingTimeCalculator.Format(flight.Start),
            WorkingTimeCalculator.Format(flight.End),
            slots);
    }

    private static int ToMinutes(DateTime value) =>
        WorkingTimeCalculator.MinutesBetween(new DateTime(2000, 1, 1), value);

    private static string Safe(string value) =>
        value.Replace('$', '_').Replace(' ', '_');

    private static string StatusName(CpSolverStatus status) => status switch
    {
        CpSolverStatus.Optimal => "Optimal",
        CpSolverStatus.Feasible => "Feasible",
        CpSolverStatus.Infeasible => "Infeasible",
        _ => status.ToString(),
    };
}
