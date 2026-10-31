using System.Text.Json;
using System.Text.Json.Serialization;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Hosting;

internal static class TemplateLibraryResponseBudget
{
    internal static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
    internal static T Check<T>(T response, int maximumBytes = ProjectReadModelLimits.MaxResponseBytes, bool countValues = true, JsonSerializerOptions? options = null)
    {
        using var stream = new BoundedStream(maximumBytes);
        JsonSerializer.Serialize(stream, response, options ?? Options);
        if (countValues)
        {
            using var document = JsonDocument.Parse(stream.GetBuffer().AsMemory(0, checked((int)stream.Length)));
            int count = 0; Count(document.RootElement);
            void Count(JsonElement element)
            {
                if (++count > ProjectReadModelLimits.MaxItems) throw new TemplateLibraryResponseBudgetException();
                if (element.ValueKind == JsonValueKind.Object) foreach (var field in element.EnumerateObject()) Count(field.Value);
                else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) Count(item);
            }
        }
        return response;
    }
    internal static void Text(string? value)
    { if (value?.Length > ProjectReadModelLimits.MaxResponseBytes) throw new TemplateLibraryResponseBudgetException(); }
    private sealed class BoundedStream(int maximum) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (count > maximum - Length) throw new TemplateLibraryResponseBudgetException();
            base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length > maximum - Length) throw new TemplateLibraryResponseBudgetException();
            base.Write(buffer);
        }
    }
}
internal sealed class TemplateLibraryResponseBudgetException : Exception;
