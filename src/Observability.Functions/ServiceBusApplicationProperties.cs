using System.Text.Json;

namespace Observability.Functions;

/// <summary>
/// Reads the application properties of a Service Bus message from the trigger's binding data. The isolated
/// worker passes them as a JSON string; other paths pass a dictionary.
/// </summary>
internal static class ServiceBusApplicationProperties
{
    public const string BindingDataKey = "ApplicationProperties";

    public static IReadOnlyDictionary<string, object?> Parse(object? value)
    {
        switch (value)
        {
            case IReadOnlyDictionary<string, object?> dictionary:
                return dictionary;
            case IDictionary<string, object?> dictionary:
                return dictionary.AsReadOnly();
            case string { Length: > 0 } json:
                try
                {
                    using var document = JsonDocument.Parse(json);
                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        return Empty;
                    }

                    var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var property in document.RootElement.EnumerateObject())
                    {
                        result[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.GetRawText();
                    }

                    return result;
                }
                catch (JsonException)
                {
                    return Empty;
                }

            default:
                return Empty;
        }
    }

    private static readonly IReadOnlyDictionary<string, object?> Empty = new Dictionary<string, object?>();
}
