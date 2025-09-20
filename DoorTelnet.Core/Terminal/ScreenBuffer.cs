using System.Text;
using System.Collections.Generic;

namespace DoorTelnet.Core.Terminal;

/// <summary>
/// Represents a simple virtual terminal screen buffer.
/// Now supports snapshots, resizing, thread-safe operations, scrolling, dirty line tracking and scrollback.
/// </summary>
public class ScreenBuffer
{
    private char[,] _chars;
    private CellAttribute[,] _attrs;
    private readonly object _sync = new();

    public int Columns { get; private set; }
    public int Rows { get; private set; }

    public int CursorX { get; private set; }
    public int CursorY { get; private set; }

    private int _savedX, _savedY;

    public CellAttribute CurrentAttribute { get; private set; } = CellAttribute.Default;

    /// <summary>
    /// Enable enhanced cleaning for stats lines to prevent AC/AT timer artifacts
    /// </summary>
    public static bool EnhancedStatsLineCleaning { get; set; } = true;

    // Dirty tracking
    private readonly HashSet<int> _dirtyLines = new();

    // Scrollback
    private readonly List<(char ch, CellAttribute attr)[]> _scrollback = new();
    public int MaxScrollbackLines { get; set; } = 2000;

    /// <summary>
    /// Fired when one or more lines have changed. Provides dirty line indices relative to the visible buffer.
    /// </summary>
    public event Action<HashSet<int>>? LinesChanged;

    /// <summary>
    /// Fired when the buffer is resized (full redraw advisable).
    /// </summary>
    public event Action? Resized;

    public ScreenBuffer(int columns, int rows)
    {
        Columns = columns;
        Rows = rows;
        _chars = new char[rows, columns];
        _attrs = new CellAttribute[rows, columns];
        ClearAll();
    }

    public void ClearAll()
    {
        HashSet<int>? changed;
        lock (_sync)
        {
            for (int y = 0; y < Rows; y++)
                for (int x = 0; x < Columns; x++)
                {
                    _chars[y, x] = ' ';
                    _attrs[y, x] = CellAttribute.Default;
                }
            CursorX = 0; CursorY = 0;
            MarkAllDirty_NoLock();
            changed = ConsumeDirtyLines_NoLock();
        }
        if (changed != null) LinesChanged?.Invoke(changed);
    }

    public void PutChar(char c)
    {
        HashSet<int>? changed = null;
        lock (_sync)
        {
            if (c == '\n')
            {
                CursorY++;
                CursorX = 0;
                ScrollIfNeeded_NoLock();
                MarkDirtyLine_NoLock(CursorY);
                changed = ConsumeDirtyLines_NoLock();
            }
            else if (c == '\r')
            {
                CursorX = 0;
            }
            else if (c == '\b' || c == (char)0x7F)
            {
                if (CursorX > 0) CursorX--; else if (CursorY > 0) { CursorY--; CursorX = Columns - 1; }
                if (CursorX >= 0 && CursorY >= 0 && CursorX < Columns && CursorY < Rows)
                {
                    _chars[CursorY, CursorX] = ' ';
                    _attrs[CursorY, CursorX] = CellAttribute.Default;
                    MarkDirtyLine_NoLock(CursorY);
                    changed = ConsumeDirtyLines_NoLock();
                }
            }
            else
            {
                if (CursorX >= 0 && CursorX < Columns && CursorY >= 0 && CursorY < Rows)
                {
                    _chars[CursorY, CursorX] = c;
                    _attrs[CursorY, CursorX] = CurrentAttribute;
                    MarkDirtyLine_NoLock(CursorY);
                }
                CursorX++;
                if (CursorX >= Columns)
                {
                    CursorX = 0;
                    CursorY++;
                }
                ScrollIfNeeded_NoLock();
                changed = ConsumeDirtyLines_NoLock();
            }
        }
        if (changed != null) LinesChanged?.Invoke(changed);
    }

    private void ScrollIfNeeded_NoLock()
    {
        if (CursorY < Rows) return;
        var lineCopy = new (char ch, CellAttribute attr)[Columns];
        for (int x = 0; x < Columns; x++)
        {
            lineCopy[x] = (_chars[0, x], _attrs[0, x]);
        }
        _scrollback.Add(lineCopy);
        if (_scrollback.Count > MaxScrollbackLines)
        {
            _scrollback.RemoveAt(0);
        }
        for (int y = 1; y < Rows; y++)
            for (int x = 0; x < Columns; x++)
            {
                _chars[y - 1, x] = _chars[y, x];
                _attrs[y - 1, x] = _attrs[y, x];
            }
        for (int x = 0; x < Columns; x++)
        {
            _chars[Rows - 1, x] = ' ';
            _attrs[Rows - 1, x] = CellAttribute.Default;
        }
        CursorY = Rows - 1;
        MarkAllDirty_NoLock();
    }

    private void MarkDirtyLine_NoLock(int y)
    {
        if (y >= 0 && y < Rows) _dirtyLines.Add(y);
    }

    private void MarkAllDirty_NoLock()
    {
        _dirtyLines.Clear();
        for (int y = 0; y < Rows; y++) _dirtyLines.Add(y);
    }

    private HashSet<int>? ConsumeDirtyLines_NoLock()
    {
        if (_dirtyLines.Count == 0) return null;
        var copy = new HashSet<int>(_dirtyLines);
        _dirtyLines.Clear();
        return copy;
    }

    public void MoveCursor(int x, int y)
    {
        HashSet<int>? changed = null;
        lock (_sync)
        {
            CursorX = Math.Clamp(x, 0, Columns - 1);
            CursorY = Math.Clamp(y, 0, Rows - 1);
            MarkDirtyLine_NoLock(CursorY);
            changed = ConsumeDirtyLines_NoLock();
        }
        if (changed != null) LinesChanged?.Invoke(changed);
    }

    public void MoveRel(int dx, int dy) => MoveCursor(CursorX + dx, CursorY + dy);

    public void EraseToEndOfLine()
    {
        HashSet<int>? changed;
        lock (_sync)
        {
            for (int x = CursorX; x < Columns; x++)
            {
                _chars[CursorY, x] = ' ';
                _attrs[CursorY, x] = CellAttribute.Default;
            }
            MarkDirtyLine_NoLock(CursorY);
            changed = ConsumeDirtyLines_NoLock();
        }
        if (changed != null) LinesChanged?.Invoke(changed);
    }

    public void EraseFromStartOfLine()
    {
        HashSet<int>? changed;
        lock (_sync)
        {
            for (int x = 0; x <= CursorX; x++)
            {
                _chars[CursorY, x] = ' ';
                _attrs[CursorY, x] = CellAttribute.Default;
            }
            MarkDirtyLine_NoLock(CursorY);
            changed = ConsumeDirtyLines_NoLock();
        }
        if (changed != null) LinesChanged?.Invoke(changed);
    }

    public void EraseLine()
    {
        HashSet<int>? changed;
        lock (_sync)
        {
            for (int x = 0; x < Columns; x++)
            {
                _chars[CursorY, x] = ' ';
                _attrs[CursorY, x] = CellAttribute.Default;
            }
            MarkDirtyLine_NoLock(CursorY);
            changed = ConsumeDirtyLines_NoLock();
        }
        if (changed != null) LinesChanged?.Invoke(changed);
    }

    /// <summary>
    /// Enhanced erase that detects and properly cleans stats line artifacts
    /// </summary>
    public void EraseToEndOfLineEnhanced()
    {
        HashSet<int>? changed;
        lock (_sync)
        {
            var currentLine = GetLine(CursorY);
            bool isStatsLine = currentLine.Contains("Hp=") || currentLine.Contains("Mp=") ||
                               currentLine.Contains("Mv=") || currentLine.Contains("Ac=") ||
                               currentLine.Contains("At=");
            if (isStatsLine)
            {
                var patterns = new[] { "/Ac=", "/At=", "Ac=", "At=" };
                foreach (var pattern in patterns)
                {
                    int patternStart = currentLine.IndexOf(pattern);
                    if (patternStart >= 0)
                    {
                        int cleanStart = patternStart;
                        for (int i = patternStart; i < currentLine.Length; i++)
                        {
                            if (currentLine[i] == ']' || currentLine[i] == ' ')
                            {
                                cleanStart = i + 1;
                                break;
                            }
                        }
                        for (int x = cleanStart; x < Columns; x++)
                        {
                            _chars[CursorY, x] = ' ';
                            _attrs[CursorY, x] = CellAttribute.Default;
                        }
                        MarkDirtyLine_NoLock(CursorY);
                        changed = ConsumeDirtyLines_NoLock();
                        goto END;
                    }
                }
                int bracketEnd = currentLine.IndexOf(']');
                if (bracketEnd >= 0)
                {
                    for (int x = bracketEnd + 1; x < Columns; x++)
                    {
                        _chars[CursorY, x] = ' ';
                        _attrs[CursorY, x] = CellAttribute.Default;
                    }
                }
                else
                {
                    for (int x = CursorX; x < Columns; x++)
                    {
                        _chars[CursorY, x] = ' ';
                        _attrs[CursorY, x] = CellAttribute.Default;
                    }
                }
            }
            else
            {
                for (int x = CursorX; x < Columns; x++)
                {
                    _chars[CursorY, x] = ' ';
                    _attrs[CursorY, x] = CellAttribute.Default;
                }
            }
            MarkDirtyLine_NoLock(CursorY);
            changed = ConsumeDirtyLines_NoLock();
        }
    END:
        if (changed != null) LinesChanged?.Invoke(changed);
    }

    public void EraseInDisplay(int mode)
    {
        HashSet<int>? changed;
        lock (_sync)
        {
            switch (mode)
            {
                case 0:
                    for (int y = CursorY; y < Rows; y++)
                    {
                        int start = y == CursorY ? CursorX : 0;
                        for (int x = start; x < Columns; x++) { _chars[y, x] = ' '; _attrs[y, x] = CellAttribute.Default; }
                        MarkDirtyLine_NoLock(y);
                    }
                    break;
                case 1:
                    for (int y = 0; y <= CursorY; y++)
                    {
                        int end = y == CursorY ? CursorX : Columns - 1;
                        for (int x = 0; x <= end; x++) { _chars[y, x] = ' '; _attrs[y, x] = CellAttribute.Default; }
                        MarkDirtyLine_NoLock(y);
                    }
                    break;
                case 2:
                    ClearAll();
                    return;
            }
            changed = ConsumeDirtyLines_NoLock();
        }
        if (changed != null) LinesChanged?.Invoke(changed);
    }

    public void EraseInLine(int mode)
    {
        switch (mode)
        {
            case 0:
                if (EnhancedStatsLineCleaning)
                    EraseToEndOfLineEnhanced();
                else
                    EraseToEndOfLine();
                break;
            case 1: EraseFromStartOfLine(); break;
            case 2: EraseLine(); break;
        }
    }

    public void SaveCursor() { lock (_sync) { _savedX = CursorX; _savedY = CursorY; } }
    public void RestoreCursor() { MoveCursor(_savedX, _savedY); }

    public void SetAttribute(params int[] codes)
    {
        HashSet<int>? changed;
        lock (_sync)
        {
            if (codes.Length == 0)
            {
                CurrentAttribute = CellAttribute.Default;
                MarkDirtyLine_NoLock(CursorY);
                changed = ConsumeDirtyLines_NoLock();
            }
            else
            {
                var attr = CurrentAttribute;
                foreach (var c in codes)
                {
                    if (c == 0) attr = CellAttribute.Default;
                    else if (c == 1) attr.Bold = true;
                    else if (c == 22) attr.Bold = false;
                    else if (c == 4) attr.Underline = true;
                    else if (c == 24) attr.Underline = false;
                    else if (c == 7) attr.Inverse = true;
                    else if (c == 27) attr.Inverse = false;
                    else if (c >= 30 && c <= 37) attr.Fg = c - 30;
                    else if (c == 39) attr.Fg = -1;
                    else if (c >= 40 && c <= 47) attr.Bg = c - 40;
                    else if (c == 49) attr.Bg = -1;
                }
                CurrentAttribute = attr;
                MarkDirtyLine_NoLock(CursorY);
                changed = ConsumeDirtyLines_NoLock();
            }
        }
        if (changed != null) LinesChanged?.Invoke(changed);
    }

    public string ToText()
    {
        lock (_sync)
        {
            var sb = new StringBuilder();
            for (int y = 0; y < Rows; y++)
            {
                for (int x = 0; x < Columns; x++) sb.Append(_chars[y, x]);
                if (y < Rows - 1) sb.Append('\n');
            }
            return sb.ToString();
        }
    }

    public (char ch, CellAttribute attr)[,] Snapshot()
    {
        lock (_sync)
        {
            var snap = new (char, CellAttribute)[Rows, Columns];
            for (int y = 0; y < Rows; y++)
                for (int x = 0; x < Columns; x++)
                    snap[y, x] = (_chars[y, x], _attrs[y, x]);
            return snap;
        }
    }

    public IEnumerable<(char ch, CellAttribute attr)[]> GetScrollback()
    {
        lock (_sync)
        {
            return _scrollback.ToArray();
        }
    }

    public (int x, int y) GetCursor()
    {
        lock (_sync) return (CursorX, CursorY);
    }

    public void Resize(int newCols, int newRows)
    {
        HashSet<int>? changed;
        bool resized = false;
        lock (_sync)
        {
            if (newCols == Columns && newRows == Rows)
            {
                return;
            }
            var newChars = new char[newRows, newCols];
            var newAttrs = new CellAttribute[newRows, newCols];
            for (int y = 0; y < newRows; y++)
                for (int x = 0; x < newCols; x++)
                {
                    if (y < Rows && x < Columns)
                    {
                        newChars[y, x] = _chars[y, x];
                        newAttrs[y, x] = _attrs[y, x];
                    }
                    else
                    {
                        newChars[y, x] = ' ';
                        newAttrs[y, x] = CellAttribute.Default;
                    }
                }
            _chars = newChars;
            _attrs = newAttrs;
            Columns = newCols;
            Rows = newRows;
            if (CursorX >= Columns) CursorX = Columns - 1;
            if (CursorY >= Rows) CursorY = Rows - 1;
            MarkAllDirty_NoLock();
            changed = ConsumeDirtyLines_NoLock();
            resized = true;
        }
        if (resized) Resized?.Invoke();
        if (changed != null) LinesChanged?.Invoke(changed);
    }

    public char GetChar(int x, int y)
    {
        lock (_sync)
        {
            if (x < 0 || y < 0 || x >= Columns || y >= Rows) return ' ';
            return _chars[y, x];
        }
    }

    public string GetLine(int y)
    {
        lock (_sync)
        {
            if (y < 0 || y >= Rows) return string.Empty;
            var chars = new char[Columns];
            for (int x = 0; x < Columns; x++) chars[x] = _chars[y, x];
            return new string(chars);
        }
    }

    public struct CellAttribute
    {
        public static CellAttribute Default => new() { Fg = -1, Bg = -1, Bold = false, Underline = false, Inverse = false };
        public int Fg { get; set; }
        public int Bg { get; set; }
        public bool Bold { get; set; }
        public bool Underline { get; set; }
        public bool Inverse { get; set; }
    }
}
