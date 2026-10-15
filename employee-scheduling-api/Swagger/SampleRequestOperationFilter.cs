using System.Text.Json;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace EmployeeScheduling.Api.Swagger;

public sealed class SampleRequestOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (!string.Equals(context.ApiDescription.RelativePath, "schedule", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var samplePath = Path.Combine(AppContext.BaseDirectory, "sample-request.json");
        if (!File.Exists(samplePath) || operation.RequestBody?.Content is null)
        {
            return;
        }

        if (!operation.RequestBody.Content.TryGetValue("application/json", out var media))
        {
            return;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(samplePath));
        media.Example = ToOpenApiAny(document.RootElement);
    }

    private static IOpenApiAny ToOpenApiAny(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => ToObject(element),
        JsonValueKind.Array => ToArray(element),
        JsonValueKind.String => new OpenApiString(element.GetString()),
        JsonValueKind.Number when element.TryGetInt64(out var whole) => new OpenApiLong(whole),
        JsonValueKind.Number => new OpenApiDouble(element.GetDouble()),
        JsonValueKind.True => new OpenApiBoolean(true),
        JsonValueKind.False => new OpenApiBoolean(false),
        _ => new OpenApiNull(),
    };

    private static OpenApiObject ToObject(JsonElement element)
    {
        var obj = new OpenApiObject();
        foreach (var property in element.EnumerateObject())
        {
            obj[property.Name] = ToOpenApiAny(property.Value);
        }

        return obj;
    }

    private static OpenApiArray ToArray(JsonElement element)
    {
        var array = new OpenApiArray();
        foreach (var item in element.EnumerateArray())
        {
            array.Add(ToOpenApiAny(item));
        }

        return array;
    }
}
