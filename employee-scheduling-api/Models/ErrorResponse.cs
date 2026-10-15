namespace EmployeeScheduling.Api.Models;

public sealed record ErrorResponse(IReadOnlyList<string> Errors);
