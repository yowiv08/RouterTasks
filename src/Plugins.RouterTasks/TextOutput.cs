using System.Text;

namespace Plugins.RouterTasks;

internal static class TextOutput
{
    public static string Summary(string? message) => LimitUtf8(message ?? "", 2048);
    public static string LimitUtf8(string value, int maximumBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maximumBytes) return value;
        var result = new StringBuilder(); var bytes = 0;
        foreach (var rune in value.EnumerateRunes())
        { if (bytes + rune.Utf8SequenceLength > maximumBytes) break; result.Append(rune.ToString()); bytes += rune.Utf8SequenceLength; }
        return result.ToString();
    }
}
