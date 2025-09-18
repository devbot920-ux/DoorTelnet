using DoorTelnet.Core.Automation;
using DoorTelnet.Core.Scripting;
using DoorTelnet.Core.Terminal;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace DoorTelnet.Core.Telnet;

public class TelnetClient
{
    private readonly ScreenBuffer _screen;
    private readonly AnsiParser _parser;
    private readonly TelnetNegotiation _negotiation;
    private readonly ScriptEngine _scriptEngine;
    private readonly RuleEngine _ruleEngine;
    private readonly ILogger<TelnetClient> _logger;
    private readonly bool _diagnostics;
    private readonly bool _rawEcho;
    private readonly bool _dumbMode;

    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _recvTask;
    private Task? _tickTask;
    private Task? _sendTask;

    private readonly ConcurrentQueue<byte> _outQueue = new();

    private int _currentCols;
    private int _currentRows;
    private DateTime _lastNawsSent = DateTime.MinValue;

    private readonly StringBuilder _currentLine = new();
    public event Action<string>? LineReceived;

    // Telnet command accumulation
    private readonly List<byte> _iacPending = new();
    private bool _inIac;

    private long _bytesReadTotal;
    private long _printableTotal;
    private int _diagPreviewCount;

    public int InterKeyDelayMs
    {
        get => _scriptEngine.InterKeyDelayMs;
        set => _scriptEngine.InterKeyDelayMs = value;
    }

    public TelnetClient(
        int cols,
        int rows,
        ScriptEngine scriptEngine,
        RuleEngine ruleEngine,
        ILogger<TelnetClient> logger,
        bool diagnostics = false,
        bool rawEcho = false,
        bool dumbMode = false)
    {
        _screen = scriptEngine
            .GetType()
            .GetField("_screen", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(scriptEngine) as ScreenBuffer
            ?? throw new InvalidOperationException();

        _parser = new AnsiParser(_screen);
        _negotiation = new TelnetNegotiation(cols, rows);
        _currentCols = cols;
        _currentRows = rows;
        _scriptEngine = scriptEngine;
        _ruleEngine = ruleEngine;
        _logger = logger;
        _diagnostics = diagnostics;
        _rawEcho = rawEcho;
        _dumbMode = dumbMode;
    }

    public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        _tcp = new TcpClient
        {
            NoDelay = true
        };
        await _tcp.ConnectAsync(host, port, cancellationToken);
        _stream = _tcp.GetStream();
        _logger.LogInformation("Connected to {host}:{port}", host, port);
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_stream == null)
        {
            throw new InvalidOperationException("Not connected");
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _recvTask = Task.Run(() => ReceiveLoop(_cts.Token));
        _sendTask = Task.Run(() => SendLoop(_cts.Token));
        _tickTask = Task.Run(() => TickLoop(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_cts == null)
        {
            return;
        }

        _cts.Cancel();
        try
        {
            if (_recvTask != null)
            {
                await _recvTask;
            }
        }
        catch { }

        try
        {
            if (_sendTask != null)
            {
                await _sendTask;
            }
        }
        catch { }

        try
        {
            if (_tickTask != null)
            {
                await _tickTask;
            }
        }
        catch { }

        _stream?.Dispose();
        _tcp?.Close();
        _logger.LogInformation("Disconnected");
    }

    private void Diag(string msg)
    {
        if (_diagnostics)
        {
            _logger.LogInformation("[TELNET] {msg}", msg);
        }
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        var buf = new byte[4096];
        var payload = new List<byte>();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await _stream!.ReadAsync(buf.AsMemory(0, buf.Length), ct);
                if (n <= 0)
                {
                    break;
                }

                _bytesReadTotal += n;
                int i = 0;
                while (i < n)
                {
                    byte b = buf[i++];

                    if (_inIac)
                    {
                        // Escaped IAC (IAC IAC)
                        if (_iacPending.Count == 0 && b == TelnetNegotiation.IAC)
                        {
                            payload.Add(255);
                            _inIac = false;
                            _iacPending.Clear();
                            continue;
                        }

                        _iacPending.Add(b);
                        if (TryProcessPendingIac())
                        {
                            if (_iacPending.Count == 0)
                            {
                                _inIac = false;
                            }
                        }

                        if (_iacPending.Count == 1 && !IsPotentialTelnetCommand(_iacPending[0]))
                        {
                            _inIac = false;
                            _iacPending.Clear();
                        }
                        continue;
                    }

                    if (b == TelnetNegotiation.IAC)
                    {
                        FlushPayload(payload);
                        _iacPending.Clear();
                        _inIac = true;
                        continue;
                    }

                    payload.Add(b);
                }

                FlushPayload(payload);

                if (_diagnostics && _bytesReadTotal % 2048 < n)
                {
                    Diag(
                        $"BytesReadTotal={_bytesReadTotal} " +
                        $"PrintableTotal={_printableTotal} " +
                        $"InIac={_inIac} PendingIac={_iacPending.Count} " +
                        $"ScreenFirstLine='{SafeFirstLine()}'"
                    );
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Receive loop error");
        }
    }

    private bool IsPotentialTelnetCommand(byte b)
    {
        return b == TelnetNegotiation.DO
            || b == TelnetNegotiation.DONT
            || b == TelnetNegotiation.WILL
            || b == TelnetNegotiation.WONT
            || b == TelnetNegotiation.SB
            || b == TelnetNegotiation.SE
            || b == 249; // GA
    }

    private bool TryProcessPendingIac()
    {
        if (_iacPending.Count == 0)
        {
            return false;
        }

        var ros = new ReadOnlySequence<byte>(_iacPending.ToArray());
        var resp = _negotiation.ProcessCommand(ros, out var consumed);

        if (consumed > 0)
        {
            if (_diagnostics)
            {
                var cmdBytes = string.Join(',', _iacPending.Take(consumed));
                Diag($"Processed IAC bytes=[{cmdBytes}] respLen={resp.Count()}");
                Diag($"Server handles echo: {_negotiation.ServerHandlesEcho}");
            }

            _iacPending.RemoveRange(0, Math.Min(consumed, _iacPending.Count));

            if (resp.Any())
            {
                EnqueueRaw(resp);
                if (_diagnostics)
                {
                    Diag($"Responded bytes=[{string.Join(',', resp)}]");
                }
            }
            return true;
        }
        return false;
    }

    private void FlushPayload(List<byte> payload)
    {
        if (payload.Count == 0)
        {
            return;
        }

        if (_diagnostics)
        {
            // Enhanced diagnostics - look for ANSI sequences
            var payloadStr = Encoding.ASCII.GetString(payload.ToArray());
            var ansiCount = payloadStr.Count(c => c == '\x1B');
            if (ansiCount > 0)
            {
                Diag($"Payload contains {ansiCount} ANSI escape sequences");
            }
        }

        // Use either ANSI parser OR BasicWrite, not both
        if (_dumbMode)
        {
            BasicWrite(payload);
        }
        else
        {
            // Always use ANSI parser in normal mode - no fallback to prevent double echoing
            _parser.Feed(CollectionsMarshal.AsSpan(payload));

            if (_diagnostics)
            {
                int visibleChars = VisibleCharEstimate();
                bool hasDisplayableContent = payload.Any(b => (b >= 32 && b < 127) || b >= 128);
                Diag($"ANSI parser processed {payload.Count} bytes, visible chars: {visibleChars}, has content: {hasDisplayableContent}");
            }
        }

        // Process line building with ANSI escape sequence stripping
        ProcessLinesFromPayload(payload);

        if (_diagnostics && _diagPreviewCount < 15)
        {
            _diagPreviewCount++;
            var preview = new string(
                payload
                    .Take(120)
                    .Select(b => b >= 32 && b < 127 ? (char)b : '.')
                    .ToArray()
            );
            var hexPreview = string.Join(" ", payload.Take(40).Select(b => b.ToString("X2")));
            Diag($"Payload[{payload.Count}] Preview='{preview}' Hex=[{hexPreview}]");
        }

        if (_rawEcho)
        {
            var echo = new string(payload.Select(b => b >= 32 && b < 127 ? (char)b : '.').ToArray());
            _logger.LogInformation("RAW:{echo}", echo);
        }

        payload.Clear();
        var text = _screen.ToText();
        _ruleEngine.Evaluate(text);
    }

    /// <summary>
    /// Process payload bytes for line building, properly stripping ANSI escape sequences
    /// </summary>
    private void ProcessLinesFromPayload(List<byte> payload)
    {
        bool inAnsiSequence = false;
        var ansiBuffer = new List<byte>();

        foreach (var ch in CollectionsMarshal.AsSpan(payload))
        {
            if (ch >= 32 && ch < 127)
            {
                _printableTotal++;
            }

            char c = (char)ch;

            // Handle ANSI escape sequences
            if (c == '\x1B') // ESC character
            {
                inAnsiSequence = true;
                ansiBuffer.Clear();
                continue;
            }

            if (inAnsiSequence)
            {
                ansiBuffer.Add(ch);

                // ANSI sequence typically ends with a letter (A-Z, a-z) or certain symbols
                // Common endings: m (SGR), A-D (cursor movement), H (cursor position), etc.
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '@')
                {
                    // End of ANSI sequence - don't add anything to the line
                    inAnsiSequence = false;
                    ansiBuffer.Clear();

                    if (_diagnostics && ansiBuffer.Count > 0)
                    {
                        var ansiSeq = string.Join("", ansiBuffer.Select(b => (char)b));
                        Diag($"Stripped ANSI sequence: ESC{ansiSeq}");
                    }
                }
                // If the sequence gets too long, abandon it (probably not a real ANSI sequence)
                else if (ansiBuffer.Count > 20)
                {
                    inAnsiSequence = false;
                    ansiBuffer.Clear();
                }
                continue;
            }

            // Regular character processing for line building
            if (c == '\n')
            {
                var line = _currentLine.ToString();
                _currentLine.Clear();
                if (!string.IsNullOrWhiteSpace(line))
                {
                    if (_diagnostics)
                    {
                        Diag($"LineReceived: '{line}'");
                    }
                    LineReceived?.Invoke(line);
                }
            }
            else if (c != '\r') // Skip carriage returns, but include everything else (except ANSI sequences)
            {
                _currentLine.Append(c);
            }
        }
    }

    private int VisibleCharEstimate()
    {
        try
        {
            var txt = _screen.ToText();
            int count = 0;
            foreach (var ch in txt)
            {
                if (ch != ' ' && ch != '\0' && ch != '\r' && ch != '\n')
                {
                    count++;
                    if (count > 50)
                    {
                        break;
                    }
                }
            }
            return count;
        }
        catch
        {
            return 0;
        }
    }

    private void BasicWrite(List<byte> bytes)
    {
        bool inEsc = false;
        var escBuffer = new List<byte>();

        foreach (var b in bytes)
        {
            if (b == 0x1B)
            {
                inEsc = true;
                escBuffer.Clear();
                continue;
            }

            if (inEsc)
            {
                escBuffer.Add(b);
                // Handle common escape sequences
                if (b >= 'A' && b <= 'Z' || b >= 'a' && b <= 'z')
                {
                    // Try to handle the escape sequence
                    ProcessEscapeSequence(escBuffer);
                    inEsc = false;
                    escBuffer.Clear();
                }
                // If escape sequence gets too long, abandon it
                else if (escBuffer.Count > 20)
                {
                    inEsc = false;
                    escBuffer.Clear();
                }
                continue;
            }

            switch (b)
            {
                case (byte)'\n':
                    _screen.PutChar('\n');
                    break;
                case (byte)'\r':
                    _screen.PutChar('\r');
                    break;
                case 0x08:
                    _screen.PutChar('\b');
                    break;
                default:
                    // Use CP437 character mapping for proper ANSI art display
                    char c = Cp437Map.ToChar(b);
                    _screen.PutChar(c);
                    break;
            }
        }
    }

    private void ProcessEscapeSequence(List<byte> escBuffer)
    {
        if (escBuffer.Count == 0) return;

        // Handle simple cases - for more complex sequences, let the main parser handle them
        var seq = System.Text.Encoding.ASCII.GetString(escBuffer.ToArray());

        // Just basic handling - most will be handled by the main ANSI parser
        switch (seq)
        {
            case "c": // Reset
                _screen.ClearAll();
                break;
            case "7": // Save cursor
                _screen.SaveCursor();
                break;
            case "8": // Restore cursor
                _screen.RestoreCursor();
                break;
                // For other sequences, we'll just ignore them since BasicWrite is a fallback
        }
    }

    private string SafeFirstLine()
    {
        try
        {
            var txt = _screen.ToText();
            var line = txt.Split('\n').FirstOrDefault() ?? string.Empty;
            if (line.Length > 80)
            {
                line = line[..80];
            }
            return line.Replace('\r', ' ').Replace('\t', ' ');
        }
        catch
        {
            return string.Empty;
        }
    }

    public void NotifyResize(int cols, int rows)
    {
        _currentCols = cols;
        _currentRows = rows;

        // Resize the screen buffer to match new dimensions
        _screen.Resize(cols, rows);

        if ((DateTime.UtcNow - _lastNawsSent).TotalMilliseconds < 500)
        {
            return; // throttle
        }
        _lastNawsSent = DateTime.UtcNow;
        var list = new List<byte>();
        _negotiation.UpdateWindowSize(cols, rows, list);
        EnqueueRaw(list);
        _logger.LogInformation("Sent NAWS {cols}x{rows}", cols, rows);
    }

    private void EnqueueRaw(IEnumerable<byte> bytes)
    {
        foreach (var b in bytes)
        {
            _outQueue.Enqueue(b);
        }
    }

    private async Task SendLoop(CancellationToken ct)
    {
        var lastKeyTime = DateTime.UtcNow;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (_scriptEngine.TryDequeueImmediate(out var ic))
                {
                    var bytesImm = Encoding.ASCII.GetBytes(new[] { ic });
                    await _stream!.WriteAsync(bytesImm, ct);
                    continue;
                }

                if (_scriptEngine.TryDequeueKey(out var c))
                {
                    var delay = TimeSpan.FromMilliseconds(InterKeyDelayMs);
                    var since = DateTime.UtcNow - lastKeyTime;
                    if (since < delay)
                    {
                        await Task.Delay(delay - since, ct);
                    }
                    lastKeyTime = DateTime.UtcNow;
                    var bytes = Encoding.ASCII.GetBytes(new[] { c });
                    await _stream!.WriteAsync(bytes, ct);
                }
                else if (_outQueue.TryDequeue(out var b))
                {
                    await _stream!.WriteAsync(new[] { b }, ct);
                }
                else
                {
                    await Task.Delay(5, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Send loop error");
        }
    }

    private async Task TickLoop(CancellationToken ct)
    {
        try
        {
            _scriptEngine.RaiseConnect();
            while (!ct.IsCancellationRequested)
            {
                _scriptEngine.RaiseTick();
                await Task.Delay(200, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // normal
        }
    }
}
