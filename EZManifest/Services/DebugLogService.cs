using System.Diagnostics;

namespace EZManifest.Services;

/// <summary>Thread-safe in-app log that also mirrors to Debug output.</summary>
public static class AppLog
{
    public static event Action<string>? LineWritten;

    public static void Write(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        Debug.WriteLine(line);

        Action<string>? handlers = LineWritten;
        if (handlers is null)
            return;

        // Invoke each subscriber separately so one UI failure cannot drop the log buffer.
        foreach (Delegate d in handlers.GetInvocationList())
        {
            try
            {
                ((Action<string>)d)(line);
            }
            catch
            {
            }
        }
    }

    public static void Write(Exception ex, string? prefix = null)
    {
        string head = string.IsNullOrWhiteSpace(prefix) ? "Error" : prefix;
        Write($"{head}: {GetRootMessage(ex)}");
        Write($"  type: {ex.GetType().FullName}");

        if (ex is AggregateException agg)
        {
            AggregateException flat = agg.Flatten();
            Write($"  aggregate count: {flat.InnerExceptions.Count}");
            for (int i = 0; i < Math.Min(flat.InnerExceptions.Count, 8); i++)
            {
                Exception inner = flat.InnerExceptions[i];
                Write($"  [{i}] {inner.GetType().Name}: {GetRootMessage(inner)}");
            }
        }
        else if (ex.InnerException is not null)
        {
            Write($"  inner: {ex.InnerException.GetType().Name}: {GetRootMessage(ex.InnerException)}");
        }

        string? stack = ex.StackTrace;
        if (!string.IsNullOrWhiteSpace(stack))
        {
            foreach (string stackLine in stack.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                Write($"  {stackLine}");
        }
    }

    public static string GetRootMessage(Exception ex)
    {
        while (true)
        {
            if (ex is AggregateException agg)
            {
                AggregateException flat = agg.Flatten();
                Exception? first = flat.InnerExceptions.Count > 0
                    ? flat.InnerExceptions[0]
                    : flat.InnerException;
                if (first is null)
                    return string.IsNullOrWhiteSpace(flat.Message) ? flat.GetType().Name : flat.Message;
                ex = first;
                continue;
            }

            return string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
        }
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.##} {units[unit]}";
    }
}

public sealed class DebugLogService
{
    private const int MaxLines = 12000;
    private readonly object _sync = new();
    private readonly List<string> _buffer = new();

    public event Action<string>? LineReceived;

    public DebugLogService()
    {
        AppLog.LineWritten += OnLineWritten;
        AppLog.Write("Debug console ready.");
    }

    private void OnLineWritten(string line)
    {
        lock (_sync)
        {
            _buffer.Add(line);
            while (_buffer.Count > MaxLines)
                _buffer.RemoveAt(0);
        }

        Action<string>? handlers = LineReceived;
        if (handlers is null)
            return;

        foreach (Delegate d in handlers.GetInvocationList())
        {
            try
            {
                ((Action<string>)d)(line);
            }
            catch
            {
            }
        }
    }

    public List<string> GetSnapshot()
    {
        lock (_sync)
            return _buffer.ToList();
    }

    public void Clear()
    {
        lock (_sync)
            _buffer.Clear();
    }
}
