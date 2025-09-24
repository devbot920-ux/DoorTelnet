using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DoorTelnet.Core.World;

/// <summary>
/// Manages a buffer of text lines for room parsing with processing state tracking
/// </summary>
public class LineBuffer
{
    private readonly Queue<BufferedLine> _lines = new();
    private readonly object _sync = new();
    private const int MaxLines = 1000;

    public void AddLine(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return;

        if (_lines.Count < 10)
        {
            var debugContent = new StringBuilder();
            foreach (char c in content.Take(50))
            {
                if (c >= 32 && c <= 126)
                {
                    debugContent.Append(c);
                }
                else if (c == '\n')
                {
                    debugContent.Append("\\n");
                }
                else if (c == '\r')
                {
                    debugContent.Append("\\r");
                }
                else if (c == '\t')
                {
                    debugContent.Append("\\t");
                }
                else
                {
                    debugContent.Append($"[{(int)c:X2}]");
                }
            }
            System.Diagnostics.Debug.WriteLine($"?? Adding to buffer: '{debugContent}'");
        }

        lock (_sync)
        {
            _lines.Enqueue(new BufferedLine { Content = content });
            while (_lines.Count > MaxLines)
            {
                _lines.Dequeue();
            }
        }
    }

    public IEnumerable<BufferedLine> GetUnprocessedLines(Func<BufferedLine, bool> processedCheck)
    {
        lock (_sync)
        {
            return _lines
                .Where(line => !processedCheck(line))
                .ToList();
        }
    }

    public IEnumerable<BufferedLine> GetAllLines()
    {
        lock (_sync)
        {
            return _lines.ToList();
        }
    }

    public void MarkProcessed(IEnumerable<BufferedLine> lines, Action<BufferedLine> markAction)
    {
        lock (_sync)
        {
            foreach (var line in lines)
            {
                markAction(line);
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _lines.Count;
            }
        }
    }

    public string GetBufferDebugInfo()
    {
        lock (_sync)
        {
            var debug = new StringBuilder();
            debug.AppendLine($"=== LINE BUFFER DEBUG ({_lines.Count} lines) ===");
            var recentLines = _lines.TakeLast(10).ToList();
            for (int i = 0; i < recentLines.Count; i++)
            {
                var line = recentLines[i];
                var flags = new List<string>();
                if (line.ProcessedForRoomDetection) flags.Add("Room");
                if (line.ProcessedForDynamicEvents) flags.Add("Dynamic");
                if (line.ProcessedForStats) flags.Add("Stats");
                if (line.ProcessedForIncarnations) flags.Add("Incarnations");

                var flagStr = flags.Count > 0
                    ? $" [{string.Join(',', flags)}]"
                    : " [Unprocessed]";

                var cleanContent = new StringBuilder();
                foreach (char c in line.Content.Take(80))
                {
                    if (c >= 32 && c <= 126)
                    {
                        cleanContent.Append(c);
                    }
                    else if (c == '\n')
                    {
                        cleanContent.Append("\\n");
                    }
                    else if (c == '\r')
                    {
                        cleanContent.Append("\\r");
                    }
                    else if (c == '\t')
                    {
                        cleanContent.Append("\\t");
                    }
                    else
                    {
                        cleanContent.Append($"[{(int)c:X2}]");
                    }
                }
                debug.AppendLine($"  {i:D2}: '{cleanContent}'{flagStr}");
            }
            debug.AppendLine("=================================");
            return debug.ToString();
        }
    }
}