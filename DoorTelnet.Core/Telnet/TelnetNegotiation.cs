using System.Buffers;
using System.Text;

namespace DoorTelnet.Core.Telnet;

/// <summary>
/// Handles Telnet option negotiation according to RFC 854 and specific options needed for BBS door games.
/// Supports: BINARY(0), ECHO(1), SGA(3), TTYPE(24), NAWS(31).
/// </summary>
public class TelnetNegotiation
{
    // Telnet command codes
    public const byte IAC = 255; // Interpret As Command
    public const byte DONT = 254;
    public const byte DO = 253;
    public const byte WONT = 252;
    public const byte WILL = 251;
    public const byte SB = 250;
    public const byte SE = 240;

    // Options
    public const byte OPT_BINARY = 0;
    public const byte OPT_ECHO = 1;
    public const byte OPT_SGA = 3;
    public const byte OPT_TTYPE = 24;
    public const byte OPT_NAWS = 31;

    private readonly HashSet<byte> _will = new();
    private readonly HashSet<byte> _do = new();

    public int Columns { get; private set; }
    public int Rows { get; private set; }

    /// <summary>
    /// True if the server is handling echo (we agreed to DO ECHO)
    /// </summary>
    public bool ServerHandlesEcho => _do.Contains(OPT_ECHO);

    private bool _ttypeSent;

    public TelnetNegotiation(int cols, int rows)
    {
        Columns = cols;
        Rows = rows;
    }

    /// <summary>
    /// Processes a block which starts after an IAC byte. Returns any response bytes required.
    /// </summary>
    public IEnumerable<byte> ProcessCommand(ReadOnlySequence<byte> sequence, out int consumed)
    {
        var reader = new SequenceReader<byte>(sequence);
        consumed = 0;
        var output = new List<byte>();
        if (!reader.TryRead(out var cmd)) return Array.Empty<byte>();
        consumed++;

        switch (cmd)
        {
            case DO:
            case DONT:
            case WILL:
            case WONT:
                if (!reader.TryRead(out var opt)) return Array.Empty<byte>();
                consumed++;
                HandleNegotiation(cmd, opt, output);
                break;
            case SB:
                // option subnegotiation until IAC SE
                if (!reader.TryRead(out var opt2)) return Array.Empty<byte>();
                consumed++;
                var subData = new List<byte>();
                while (reader.TryRead(out var b))
                {
                    consumed++;
                    if (b == IAC)
                    {
                        if (reader.TryRead(out var maybeSe))
                        {
                            consumed++;
                            if (maybeSe == SE)
                                break; // end of subnegotiation
                            // Escaped IAC inside subnegotiation
                            subData.Add(IAC);
                            subData.Add(maybeSe);
                        }
                        else break;
                    }
                    else
                        subData.Add(b);
                }
                HandleSubNegotiation(opt2, subData, output);
                break;
            default:
                // Ignore other standalone commands for now.
                break;
        }
        return output;
    }

    private void HandleNegotiation(byte verb, byte opt, List<byte> response)
    {
        switch (verb)
        {
            case DO:
                if (ShouldWeWill(opt))
                {
                    if (_will.Add(opt))
                        Emit(response, IAC, WILL, opt);
                    // For NAWS we also send the current size after acknowledging
                    if (opt == OPT_NAWS)
                        EmitNaws(response);
                }
                else
                {
                    Emit(response, IAC, WONT, opt);
                }
                break;
            case DONT:
                if (_will.Remove(opt))
                    Emit(response, IAC, WONT, opt);
                break;
            case WILL:
                if (ShouldWeDo(opt))
                {
                    if (_do.Add(opt))
                        Emit(response, IAC, DO, opt);
                    if (opt == OPT_TTYPE)
                    {
                        // request terminal type via subnegotiation: SEND
                        Emit(response, IAC, SB, OPT_TTYPE, 1, IAC, SE);
                    }
                }
                else
                {
                    Emit(response, IAC, DONT, opt);
                }
                break;
            case WONT:
                if (_do.Remove(opt))
                    Emit(response, IAC, DONT, opt);
                break;
        }
    }

    private bool ShouldWeWill(byte opt) => opt switch
    {
        OPT_BINARY or OPT_SGA or OPT_NAWS or OPT_TTYPE => true,
        _ => false
    };

    private bool ShouldWeDo(byte opt) => opt switch
    {
        OPT_ECHO or OPT_SGA or OPT_BINARY or OPT_TTYPE => true,
        _ => false
    };

    private void HandleSubNegotiation(byte opt, List<byte> data, List<byte> response)
    {
        if (opt == OPT_TTYPE)
        {
            // Expect SEND signal (1)
            if (!_ttypeSent)
            {
                _ttypeSent = true;
                // IAC SB TTYPE 0 'V' 'T' '1' '0' '0' IAC SE (0 = IS)
                var term = Encoding.ASCII.GetBytes("VT100");
                response.Add(IAC); response.Add(SB); response.Add(OPT_TTYPE); response.Add(0);
                response.AddRange(term);
                response.Add(IAC); response.Add(SE);
            }
        }
    }

    private void Emit(List<byte> list, params byte[] bytes) => list.AddRange(bytes);

    private void EmitNaws(List<byte> list)
    {
        // IAC SB NAWS width high,width low,height high,height low IAC SE
        ushort w = (ushort)Columns;
        ushort h = (ushort)Rows;
        list.Add(IAC); list.Add(SB); list.Add(OPT_NAWS);
        list.Add((byte)(w >> 8)); list.Add((byte)(w & 0xFF));
        list.Add((byte)(h >> 8)); list.Add((byte)(h & 0xFF));
        list.Add(IAC); list.Add(SE);
    }

    public void UpdateWindowSize(int cols, int rows, List<byte>? outImmediate = null)
    {
        Columns = cols; Rows = rows;
        var tmp = new List<byte>();
        EmitNaws(tmp);
        if (outImmediate != null) outImmediate.AddRange(tmp);
    }
}
