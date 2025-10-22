using System.Text;

namespace DoorTelnet.Core.Terminal;

/// <summary>
/// Minimal ANSI/VT100 parser handling a subset needed for BBS door games.
/// Supports CSI cursor moves, erase functions, SGR (including bright colors), save/restore cursor.
/// Enhanced with color logging for diagnostics.
/// </summary>
public class AnsiParser
{
    private enum State { Text, Esc, Csi }
    private State _state = State.Text;
    private readonly StringBuilder _csi = new();

    private readonly ScreenBuffer _buffer;
    
    // Color logging support
    public static bool EnableColorLogging { get; set; } = false;
    private static readonly StringBuilder _recentText = new();
    private static int _colorChangeCount = 0;
    private static string _lastColorDescription = ""; // Track the last color for follow-up logging

    public AnsiParser(ScreenBuffer buffer) => _buffer = buffer;

    public void Feed(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            char ch = Cp437Map.ToChar(b);
            switch (_state)
            {
                case State.Text:
                    if (ch == '\x1B') _state = State.Esc;
                    else
                    {
                        _buffer.PutChar(ch);
                        if (EnableColorLogging && ch >= 32 && ch <= 126)
                        {
                            _recentText.Append(ch);
                        }
                    }
                    break;
                case State.Esc:
                    if (ch == '[') { _csi.Clear(); _state = State.Csi; }
                    else if (ch == '7') { _buffer.SaveCursor(); _state = State.Text; }
                    else if (ch == '8') { _buffer.RestoreCursor(); _state = State.Text; }
                    else if (ch == 'c') { _buffer.ClearAll(); _state = State.Text; } // Reset (RIS)
                    else if (ch == 'D') { _buffer.MoveRel(0, 1); _state = State.Text; } // Index (IND) 
                    else if (ch == 'E') { _buffer.MoveCursor(0, _buffer.CursorY + 1); _state = State.Text; } // Next Line (NEL)
                    else if (ch == 'M') { _buffer.MoveRel(0, -1); _state = State.Text; } // Reverse Index (RI)
                    else { _state = State.Text; } // ignore other ESC sequences for now
                    break;
                case State.Csi:
                    if ((ch >= '0' && ch <= '9') || ch == ';' || ch == '?' || ch == ' ')
                    {
                        _csi.Append(ch);
                    }
                    else
                    {
                        HandleCsi(_csi.ToString(), ch);
                        _state = State.Text;
                    }
                    break;
            }
        }
    }

    private void HandleCsi(string param, char final)
    {
        int[] nums = ParseParams(param);
        if (final == 'm') { ApplySgr(nums); return; }
        if (nums.Length == 0) nums = new[] { 1 };
        switch (final)
        {
            case 'A': // CUU - Cursor Up
                _buffer.MoveRel(0, -nums[0]);
                break;
            case 'B': // CUD - Cursor Down
                _buffer.MoveRel(0, nums[0]);
                break;
            case 'C': // CUF - Cursor Forward
                _buffer.MoveRel(nums[0], 0);
                break;
            case 'D': // CUB - Cursor Backward
                _buffer.MoveRel(-nums[0], 0);
                break;
            case 'H': // CUP - Cursor Position
            case 'f': // HVP - Horizontal and Vertical Position
                {
                    int r = nums.Length > 0 ? nums[0] : 1;
                    int c = nums.Length > 1 ? nums[1] : 1;
                    _buffer.MoveCursor(c - 1, r - 1);
                }
                break;
            case 'J': // ED - Erase in Display
                {
                    int mode = nums.Length > 0 ? nums[0] : 0;
                    _buffer.EraseInDisplay(mode);
                }
                break;
            case 'K': // EL - Erase in Line
                {
                    int mode = nums.Length > 0 ? nums[0] : 0;
                    _buffer.EraseInLine(mode);
                }
                break;
            case 'L': // IL - Insert Lines
                // Not commonly implemented in simple terminals, ignore for now
                break;
            case 'M': // DL - Delete Lines
                // Not commonly implemented in simple terminals, ignore for now
                break;
            case 'P': // DCH - Delete Characters
                // Not commonly implemented in simple terminals, ignore for now
                break;
            case 'S': // SU - Scroll Up
                // Not commonly implemented in simple terminals, ignore for now
                break;
            case 'T': // SD - Scroll Down
                // Not commonly implemented in simple terminals, ignore for now
                break;
            case 's': // save cursor (ANSI.SYS)
                _buffer.SaveCursor();
                break;
            case 'u': // restore cursor (ANSI.SYS)
                _buffer.RestoreCursor();
                break;
            case 'G': // CHA - Cursor Horizontal Absolute
                {
                    int col = nums.Length > 0 ? nums[0] : 1;
                    _buffer.MoveCursor(col - 1, _buffer.CursorY);
                }
                break;
            case 'd': // VPA - Vertical Line Position Absolute
                {
                    int row = nums.Length > 0 ? nums[0] : 1;
                    _buffer.MoveCursor(_buffer.CursorX, row - 1);
                }
                break;
        }
    }

    private void ApplySgr(int[] codes)
    {
        if (codes.Length == 0) { _buffer.SetAttribute(0); return; }
        
        // COLOR LOGGING: Show what text was rendered with the PREVIOUS color, then start tracking the new color
        if (EnableColorLogging)
        {
            var capturedText = GetRecentText();
            if (!string.IsNullOrEmpty(_lastColorDescription) && !string.IsNullOrEmpty(capturedText))
            {
                System.Diagnostics.Debug.WriteLine($"   ↳ Rendered: '{capturedText}'");
            }
            _recentText.Clear();
            LogColorChange(codes);
        }
        
        // Expand bright color codes to standard ones with a flag if desired.
        // For now map 90-97 to 30-37 + bold, 100-107 to 40-47 + bold.
        var expanded = new List<int>();
        for (int i = 0; i < codes.Length; i++)
        {
            int c = codes[i];
            if (c == 38 || c == 48)
            {
                bool isFg = c == 38;
                if (i + 1 < codes.Length)
                {
                    int mode = codes[++i];
                    if (mode == 2 && i + 3 < codes.Length)
                    {
                        // truecolor r;g;b
                        int r = codes[++i];
                        int g = codes[++i];
                        int b = codes[++i];
                        // Map truecolor to nearest 8-color for now
                        int idx = Approx8Color(r, g, b);
                        
                        if (EnableColorLogging)
                        {
                            System.Diagnostics.Debug.WriteLine($"? TRUECOLOR: RGB({r},{g},{b}) ? Color {idx} | {(isFg ? "Foreground" : "Background")} | Text: '{GetRecentText()}'");
                        }
                        
                        expanded.Add(isFg ? (30 + idx) : (40 + idx));
                    }
                    else if (mode == 5 && i + 1 < codes.Length)
                    {
                        int pal = codes[++i];
                        int idx = Pal256ToBasic(pal);
                        
                        if (EnableColorLogging)
                        {
                            System.Diagnostics.Debug.WriteLine($"? PALETTE: Pal256[{pal}] ? Color {idx} | {(isFg ? "Foreground" : "Background")} | Text: '{GetRecentText()}'");
                        }
                        
                        expanded.Add(isFg ? (30 + idx) : (40 + idx));
                    }
                }
            }
            else if (c == 0 || c == 1 || c == 4 || c == 7 || c == 22 || c == 24 || c == 27 || (c >= 30 && c <= 37) || (c >= 40 && c <= 47) || c == 39 || c == 49)
            {
                expanded.Add(c);
            }
            else if (c >= 90 && c <= 97)
            {
                expanded.Add(1); expanded.Add(c - 60);
            }
            else if (c >= 100 && c <= 107)
            {
                expanded.Add(1); expanded.Add(c - 60);
            }
        }
        _buffer.SetAttribute([.. expanded]);
    }

    private static void LogColorChange(int[] codes)
    {
        _colorChangeCount++;
        
        // Build a human-readable description of the color change
        var description = new StringBuilder();
        description.Append($"⚡ COLOR #{_colorChangeCount}: SGR[{string.Join(";", codes)}] ");
        
        foreach (var code in codes)
        {
            if (code == 0)
                description.Append("RESET ");
            else if (code == 1)
                description.Append("BOLD ");
            else if (code >= 30 && code <= 37)
                description.Append($"FG:{GetColorName(code - 30)} ");
            else if (code >= 40 && code <= 47)
                description.Append($"BG:{GetColorName(code - 40)} ");
            else if (code >= 90 && code <= 97)
                description.Append($"BRIGHT-FG:{GetColorName(code - 90)} ");
            else if (code >= 100 && code <= 107)
                description.Append($"BRIGHT-BG:{GetColorName(code - 100)} ");
            else if (code == 38)
                description.Append("FG:(extended) ");
            else if (code == 48)
                description.Append("BG:(extended) ");
        }
        
        _lastColorDescription = description.ToString();
        System.Diagnostics.Debug.WriteLine(_lastColorDescription);
    }

    private static string GetColorName(int colorIndex)
    {
        return colorIndex switch
        {
            0 => "Black",
            1 => "Red",
            2 => "Green",
            3 => "Yellow",
            4 => "Blue",
            5 => "Magenta",
            6 => "Cyan",
            7 => "White",
            _ => $"Unknown({colorIndex})"
        };
    }

    private static string GetRecentText()
    {
        if (_recentText.Length == 0) return "";
        
        var text = _recentText.ToString();
        // Don't clear here anymore - it's cleared in ApplySgr before logging
        
        // Trim and limit length for logging
        text = text.Trim();
        if (text.Length > 50)
            text = text.Substring(0, 50) + "...";
        
        return text;
    }

    private static int Pal256ToBasic(int pal)
    {
        if (pal < 16)
        {
            // standard + bright
            int idx = pal % 8;
            return idx; // bold handled separately if needed
        }
        // approximate by luminance
        return pal % 8;
    }

    private static int Approx8Color(int r, int g, int b)
    {
        // Map to 16-color palette index base (0-7 basic). We'll set bold for bright approximation externally not implemented here.
        // Use perceived luminance and hue sectors.
        if (r == g && g == b)
        {
            if (r < 32) return 0; // black
            if (r > 200) return 7; // white
            if (r > 128) return 7; // light gray
            return 0; // dark gray approximated as black for 8-color
        }
        // Determine dominant
        if (r >= g && r >= b)
        {
            if (g >= b)
            {
                // red + green => yellow
                if (r - b < 60) return 3; // yellowish
                return 1; // red
            }
            else return 1; // red
        }
        if (g >= r && g >= b)
        {
            if (r >= b) return 3; // yellowish
            if (b >= r) return 6; // cyan (g+b)
            return 2; // green
        }
        // Blue dominant
        if (r >= g) return 5; // magenta
        if (g >= r) return 6; // cyan
        return 4; // blue
    }

    private static int[] ParseParams(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return Array.Empty<int>();
        var parts = s.Split(';', StringSplitOptions.None); // keep empties
        var list = new List<int>();
        foreach (var p in parts)
        {
            if (p.Length == 0) { list.Add(0); continue; }
            if (int.TryParse(p, out var v)) list.Add(v);
        }
        return [.. list];
    }
}
