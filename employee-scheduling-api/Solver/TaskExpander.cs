using EmployeeScheduling.Api.Models;

namespace EmployeeScheduling.Api.Solver;

public static class TaskExpander
{
    public static List<TaskDefinition> Expand(IReadOnlyList<FlightDefinition> flights)
    {
        var tasks = new List<TaskDefinition>();
        foreach (var flight in flights)
        {
            var group = 1;
            foreach (var requirement in flight.RequiredEmployees ?? new())
            {
                for (var slot = 1; slot <= requirement.NumberOfEmployees; slot++)
                {
                    tasks.Add(new TaskDefinition
                    {
                        Id = $"{flight.Id}${group}_{slot}",
                        FlightId = flight.Id,
                        Start = flight.Start,
                        End = flight.End,
                        RequiredSkills = requirement.Skills ?? new(),
                    });
                }

                group++;
            }
        }

        return tasks;
    }
}
