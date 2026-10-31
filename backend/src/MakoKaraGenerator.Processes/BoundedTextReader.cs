using System.Runtime.CompilerServices;
using System.Text;

namespace MakoKaraGenerator.Processes;

/// <summary>Pipe readers whose retained allocation is independent of producer output length.</summary>
public static class BoundedTextReader
{
    /// <summary>Drain a pipe while retaining a bounded prefix, or reject metadata that exceeds its limit.</summary>
    public static async Task<string> ReadToEndAsync(TextReader reader, int maximumCharacters, bool rejectOverflow,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCharacters);
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        StringBuilder text = new(Math.Min(maximumCharacters, 4096));
        char[] buffer = new char[4096]; bool truncated = false;
        while (true)
        {
            int count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            int keep = Math.Min(count, maximumCharacters - text.Length);
            text.Append(buffer, 0, keep);
            if (keep == count) continue;
            if (rejectOverflow) throw new IOException("Process metadata exceeds its declared character budget.");
            truncated = true;
        }
        return MarkTruncated(text.ToString(), maximumCharacters, truncated);
    }

    /// <summary>Read complete lines without allocating an unbounded unterminated line; excess text is drained.</summary>
    public static async IAsyncEnumerable<string> ReadLinesAsync(TextReader reader, int maximumLineCharacters,
        bool rejectOverflow = false, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLineCharacters);
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        StringBuilder line = new(Math.Min(maximumLineCharacters, 4096));
        char[] buffer = new char[4096]; bool truncated = false, skipNewline = false;
        while (true)
        {
            int count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            for (int index = 0; index < count; index++)
            {
                char value = buffer[index];
                if (skipNewline && value == '\n') { skipNewline = false; continue; }
                skipNewline = false;
                if (value is '\r' or '\n')
                {
                    yield return MarkTruncated(line.ToString(), maximumLineCharacters, truncated);
                    line.Clear(); truncated = false; skipNewline = value == '\r';
                }
                else if (line.Length < maximumLineCharacters) line.Append(value);
                else if (rejectOverflow) throw new IOException("Process progress line exceeds its declared character budget.");
                else truncated = true;
            }
        }
        if (line.Length > 0 || truncated) yield return MarkTruncated(line.ToString(), maximumLineCharacters, truncated);
    }

    private static string MarkTruncated(string text, int maximum, bool truncated)
    {
        if (!truncated) return text;
        const string suffix = " [truncated]";
        int keep = Math.Max(0, maximum - suffix.Length);
        return text[..Math.Min(keep, text.Length)] + suffix[..Math.Min(maximum, suffix.Length)];
    }
}
