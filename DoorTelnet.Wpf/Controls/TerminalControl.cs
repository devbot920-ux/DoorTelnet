using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DoorTelnet.Core.Terminal;
using DoorTelnet.Core.Scripting;
using DoorTelnet.Core.Telnet;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;

namespace DoorTelnet.Wpf.Controls;

/// <summary>
/// Enhanced terminal control (Stage 2 advanced):
/// - Event driven redraw using ScreenBuffer.LinesChanged/Resized
/// - Dirty line incremental rendering
/// - Scrollback (PageUp/PageDown)
/// - Cursor styles (underscore, block, pipe, hash, dot, plus) from config: terminal:cursorStyle
/// - Text selection + copy (Ctrl+C / right-click copy)
/// </summary>
public class TerminalControl : FrameworkElement
{
    // Font size DP for binding if needed
    public static readonly DependencyProperty TerminalFontSizeProperty = DependencyProperty.Register(
        nameof(TerminalFontSize), typeof(double), typeof(TerminalControl),
        new FrameworkPropertyMetadata(14.0, FrameworkPropertyMetadataOptions.AffectsRender, OnFontSizeChanged));

    private static void OnFontSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TerminalControl tc)
        {
            tc.MeasureFontMetrics();
            tc.UpdateTerminalGeometry(); // auto recompute cols/rows on font size change
            tc.InvalidateVisual();
        }
    }

    public double TerminalFontSize
    {
        get => (double)GetValue(TerminalFontSizeProperty);
        set => SetValue(TerminalFontSizeProperty, value);
    }

    private ScreenBuffer? _buffer;
    private ScriptEngine? _script;
    private TelnetClient? _telnet;
    private IConfiguration? _config;

    // Rendering
    private Typeface _typeface = new("Consolas");
    private double _charWidth = 8;
    private double _charHeight = 16;

    // Dirty tracking
    private readonly HashSet<int> _dirtyLines = new();
    private bool _fullRedraw = true;
    private bool _renderScheduled = false; // coalescing flag
    private DateTime _lastScheduled = DateTime.MinValue;
    private static readonly TimeSpan MinScheduleInterval = TimeSpan.FromMilliseconds(15);

    // Snapshot cache
    private (char ch, ScreenBuffer.CellAttribute attr)[,]? _snapshot;

    // Scrollback
    private int _scrollOffset = 0; // 0 = bottom (live), positive = lines above
    private List<(char ch, ScreenBuffer.CellAttribute attr)[]> _scrollbackCache = new();

    // Cursor
    private bool _cursorVisible = true;
    private DateTime _lastCursorToggle = DateTime.UtcNow;
    private TimeSpan _cursorBlinkInterval = TimeSpan.FromMilliseconds(550);
    private string _cursorStyle = "underscore";

    // Selection
    private Point? _selectionAnchor; // cell coords
    private Point? _selectionEnd; // cell coords
    private bool _isSelecting;

    // Performance
    private const int MaxFps = 60;
    private DateTime _lastRender = DateTime.MinValue;

    private DateTime _lastNaws = DateTime.MinValue;
    private static readonly TimeSpan NawsThrottle = TimeSpan.FromMilliseconds(400);

    public TerminalControl()
    {
        Focusable = true;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SnapsToDevicePixels = true;
    }

    #region Initialization
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ResolveDependencies();
        MeasureFontMetrics();
        HookBufferEvents();
        ApplyConfig();
        InvalidateVisual();
        Focus();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        UnhookBufferEvents();
    }

    private void ResolveDependencies()
    {
        var app = (App)Application.Current;
        var hostField = typeof(App).GetField("_host", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var host = hostField?.GetValue(app) as Microsoft.Extensions.Hosting.IHost;
        if (host != null)
        {
            _buffer = host.Services.GetService<ScreenBuffer>();
            _script = host.Services.GetService<ScriptEngine>();
            _telnet = host.Services.GetService<TelnetClient>();
            _config = host.Services.GetService<IConfiguration>();
        }
        _snapshot = _buffer?.Snapshot();
        _scrollbackCache = _buffer?.GetScrollback().Select(line => line.ToArray()).ToList() ?? new();
    }

    private void MeasureFontMetrics()
    {
        var ft = CreateText("W", Brushes.White);
        _charWidth = ft.WidthIncludingTrailingWhitespace;
        _charHeight = ft.Height;
    }

    private void ApplyConfig()
    {
        var style = _config?["terminal:cursorStyle"] ?? "underscore";
        _cursorStyle = style.ToLowerInvariant();
    }

    private void HookBufferEvents()
    {
        if (_buffer == null) return;
        _buffer.LinesChanged += OnLinesChanged;
        _buffer.Resized += OnBufferResized;
    }

    private void UnhookBufferEvents()
    {
        if (_buffer == null) return;
        _buffer.LinesChanged -= OnLinesChanged;
        _buffer.Resized -= OnBufferResized;
    }

    private void OnLinesChanged(HashSet<int> lines)
    {
        lock (_dirtyLines)
        {
            foreach (var l in lines)
            {
                _dirtyLines.Add(l);
            }
            // If a render is already scheduled, just accumulate
            if (_renderScheduled)
            {
                return;
            }
            // Throttle scheduling to avoid overloading UI thread
            var now = DateTime.UtcNow;
            if (now - _lastScheduled < MinScheduleInterval)
            {
                // Delay scheduling slightly
                _renderScheduled = true;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _renderScheduled = false;
                    ProcessPendingLines();
                }), System.Windows.Threading.DispatcherPriority.Background);
                return;
            }
            _lastScheduled = now;
            _renderScheduled = true;
        }
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _renderScheduled = false;
            ProcessPendingLines();
        }), System.Windows.Threading.DispatcherPriority.Render);
    }

    private void ProcessPendingLines()
    {
        // Snapshot & scrollback capture on UI thread to eliminate contention on worker thread
        if (_buffer == null)
        {
            return;
        }
        _snapshot = _buffer.Snapshot();
        _scrollbackCache = _buffer.GetScrollback().Select(l => l.ToArray()).ToList();
        InvalidateVisual();
    }

    private void OnBufferResized()
    {
        _fullRedraw = true;
        // Force snapshot refresh through the coalesced path
        OnLinesChanged(new HashSet<int>(Enumerable.Range(0, _buffer?.Rows ?? 0)));
    }
    #endregion

    #region Rendering Helpers
    private FormattedText CreateText(string s, Brush brush)
    {
        return new FormattedText(
            s,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            _typeface,
            TerminalFontSize <= 0 ? 14 : TerminalFontSize,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (_buffer == null || _snapshot == null)
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(RenderSize));
            return;
        }

        // Cursor blink toggle
        var now = DateTime.UtcNow;
        if (now - _lastCursorToggle > _cursorBlinkInterval)
        {
            _cursorVisible = !_cursorVisible;
            _lastCursorToggle = now;
        }

        // Fill background
        dc.DrawRectangle(Brushes.Black, null, new Rect(RenderSize));

        int rows = _buffer.Rows;
        int cols = _buffer.Columns;
        double padX = 2;
        double padY = 2;

        // Determine visible lines considering scrollback
        // scrollOffset = 0 => last rows of live buffer
        // When positive, we overlay scrollback + visible live lines
        var visibleLines = new List<(int? bufferRow, (char ch, ScreenBuffer.CellAttribute attr)[]? scrollLine)>();

        if (_scrollOffset == 0)
        {
            for (int r = 0; r < rows; r++)
            {
                visibleLines.Add((r, null));
            }
        }
        else
        {
            // Combine scrollback tail + current buffer top portion
            int totalNeeded = rows;
            // Start index in scrollback (from end)
            for (int i = _scrollOffset; i < _scrollOffset + rows; i++)
            {
                int idx = _scrollbackCache.Count - 1 - i;
                if (idx >= 0 && idx < _scrollbackCache.Count)
                {
                    visibleLines.Add((null, _scrollbackCache[idx]));
                }
                else
                {
                    visibleLines.Add((null, null));
                }
            }
        }

        // Render lines
        double y = padY;
        for (int lineIndex = 0; lineIndex < visibleLines.Count && y + _charHeight <= ActualHeight; lineIndex++)
        {
            var (bufferRow, scrollLine) = visibleLines[lineIndex];
            (char ch, ScreenBuffer.CellAttribute attr)[] lineCells;

            if (bufferRow.HasValue)
            {
                lineCells = new (char, ScreenBuffer.CellAttribute)[cols];
                for (int c = 0; c < cols; c++)
                {
                    lineCells[c] = _snapshot[bufferRow.Value, c];
                }
            }
            else if (scrollLine != null)
            {
                lineCells = scrollLine;
            }
            else
            {
                lineCells = Enumerable.Repeat((' ', ScreenBuffer.CellAttribute.Default), cols).ToArray();
            }

            double x = padX;
            int col = 0;
            while (col < lineCells.Length)
            {
                var (ch, attr) = lineCells[col];
                int start = col;
                int fg = attr.Fg;
                int bg = attr.Bg;
                bool inverse = attr.Inverse;
                bool bold = attr.Bold;
                var sb = new StringBuilder();
                while (col < lineCells.Length)
                {
                    var (ch2, attr2) = lineCells[col];
                    if (ch2 == '\0') ch2 = ' ';
                    if (attr2.Fg != fg || attr2.Bg != bg || attr2.Inverse != inverse || attr2.Bold != bold) break;
                    sb.Append(ch2);
                    col++;
                }
                var run = sb.ToString().TrimEnd();
                if (run.Length > 0)
                {
                    var fgBrush = GetForegroundBrush(fg, bold, inverse, bg);
                    var bgBrush = GetBackgroundBrush(bg, inverse, bold, fg);
                    if (bg >= 0 || inverse)
                    {
                        dc.DrawRectangle(bgBrush, null, new Rect(x, y, run.Length * _charWidth, _charHeight));
                    }
                    dc.DrawText(CreateText(run, fgBrush), new Point(x, y));
                }
                x += (col - start) * _charWidth;
            }

            // Selection highlight
            if (HasSelection && bufferRow.HasValue)
            {
                HighlightSelection(dc, bufferRow.Value, y, padX);
            }
            y += _charHeight;
        }

        // Cursor (only when not scrolled back)
        if (_scrollOffset == 0 && _cursorVisible)
        {
            var cursor = _buffer.GetCursor();
            DrawCursor(dc, cursor.x, cursor.y, padX, padY);
        }
    }

    private void HighlightSelection(DrawingContext dc, int row, double y, double padX)
    {
        if (!_selectionAnchor.HasValue || !_selectionEnd.HasValue) return;
        var (ax, ay) = ((int)_selectionAnchor.Value.X, (int)_selectionAnchor.Value.Y);
        var (bx, by) = ((int)_selectionEnd.Value.X, (int)_selectionEnd.Value.Y);
        if (ay > by || (ay == by && ax > bx))
        {
            (ax, bx) = (bx, ax);
            (ay, by) = (by, ay);
        }
        if (row < ay || row > by) return;
        int startX = row == ay ? ax : 0;
        int endX = row == by ? bx : (_buffer?.Columns ?? 0) - 1;
        if (endX < startX) return;
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(80, 100, 160, 255)), null,
            new Rect(padX + startX * _charWidth, y, (endX - startX + 1) * _charWidth, _charHeight));
    }

    private void DrawCursor(DrawingContext dc, int cx, int cy, double padX, double padY)
    {
        if (_buffer == null) return;
        if (cx < 0 || cy < 0 || cx >= _buffer.Columns || cy >= _buffer.Rows) return;
        double x = padX + cx * _charWidth;
        double y = padY + cy * _charHeight;
        Brush brush = Brushes.LightGray;
        switch (_cursorStyle)
        {
            case "block":
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(160, 200, 200, 200)), null, new Rect(x, y, _charWidth, _charHeight));
                break;
            case "pipe":
                dc.DrawRectangle(brush, null, new Rect(x, y, Math.Max(1, _charWidth * 0.18), _charHeight));
                break;
            case "hash":
                dc.DrawRectangle(Brushes.Transparent, null, new Rect(x, y, _charWidth, _charHeight));
                dc.DrawLine(new Pen(brush, 1), new Point(x, y + _charHeight / 2), new Point(x + _charWidth, y + _charHeight / 2));
                dc.DrawLine(new Pen(brush, 1), new Point(x + _charWidth / 2, y), new Point(x + _charWidth / 2, y + _charHeight));
                break;
            case "dot":
                dc.DrawEllipse(brush, null, new Point(x + _charWidth / 2, y + _charHeight - 3), 2, 2);
                break;
            case "plus":
                dc.DrawLine(new Pen(brush, 1), new Point(x + _charWidth / 2, y + 2), new Point(x + _charWidth / 2, y + _charHeight - 2));
                dc.DrawLine(new Pen(brush, 1), new Point(x + 2, y + _charHeight / 2), new Point(x + _charWidth - 2, y + _charHeight / 2));
                break;
            default: // underscore
                dc.DrawRectangle(brush, null, new Rect(x, y + _charHeight - 2, Math.Max(2, _charWidth * 0.7), 2));
                break;
        }
    }

    private int NormalizeColor(int c) => (c < 0 || c >= 16) ? 7 : c;
    private static readonly SolidColorBrush[] Palette = BuildPalette();
    private static SolidColorBrush TransparentBlack = new SolidColorBrush(Color.FromRgb(0,0,0));
    private static SolidColorBrush[] BuildPalette()
    {
        var list = new List<SolidColorBrush>();
        Color[] colors =
        {
            Color.FromRgb(0,0,0), Color.FromRgb(128,0,0), Color.FromRgb(0,128,0), Color.FromRgb(128,128,0),
            Color.FromRgb(0,0,128), Color.FromRgb(128,0,128), Color.FromRgb(0,128,128), Color.FromRgb(192,192,192),
            Color.FromRgb(128,128,128), Color.FromRgb(255,0,0), Color.FromRgb(0,255,0), Color.FromRgb(255,255,0),
            Color.FromRgb(0,0,255), Color.FromRgb(255,0,255), Color.FromRgb(0,255,255), Color.FromRgb(255,255,255)
        };
        foreach (var c in colors) { var b = new SolidColorBrush(c); b.Freeze(); list.Add(b); }
        return list.ToArray();
    }
    private Brush GetForegroundBrush(int fg, bool bold, bool inverse, int bg)
    {
        if (inverse)
        {
            // When inverse, background becomes foreground – if bg not set use default light gray
            if (bg >= 0)
            {
                return Palette[NormalizeColor(bg + (bold && bg < 8 ? 8 : 0)) % Palette.Length];
            }
            return Brushes.Gainsboro; // fallback
        }
        int idx = fg < 0 ? 7 : NormalizeColor(fg + (bold && fg < 8 ? 8 : 0));
        return Palette[idx % Palette.Length];
    }

    private Brush GetBackgroundBrush(int bg, bool inverse, bool bold, int fg)
    {
        if (inverse)
        {
            // Foreground becomes background; if fg unset use black
            if (fg >= 0)
            {
                return Palette[NormalizeColor(fg + (bold && fg < 8 ? 8 : 0)) % Palette.Length];
            }
            return TransparentBlack;
        }
        if (bg < 0)
        {
            return TransparentBlack; // treat -1 as black (no rectangle drawn unless inverse handled separately)
        }
        return Palette[NormalizeColor(bg) % Palette.Length];
    }
    #endregion

    #region Input Handling
    protected override void OnPreviewTextInput(TextCompositionEventArgs e)
    {
        base.OnPreviewTextInput(e);
        if (_script != null && _scrollOffset == 0)
        {
            foreach (var c in e.Text)
            {
                _script.EnqueueImmediate(c);
            }
        }
        e.Handled = true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.PageUp)
        {
            _scrollOffset = Math.Min(_scrollOffset + 5, _scrollbackCache.Count);
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.PageDown)
        {
            _scrollOffset = Math.Max(0, _scrollOffset - 5);
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (_script == null || _scrollOffset != 0) return; // do not send keys while scrolled back
        switch (e.Key)
        {
            case Key.Enter: _script.EnqueueImmediate('\r'); e.Handled = true; break;
            case Key.Back: _script.EnqueueImmediate('\b'); e.Handled = true; break;
            case Key.Tab: _script.EnqueueImmediate('\t'); e.Handled = true; break;
            case Key.Escape: _script.EnqueueImmediate((char)27); e.Handled = true; break;
            case Key.Left: SendEscape("[D"); e.Handled = true; break;
            case Key.Right: SendEscape("[C"); e.Handled = true; break;
            case Key.Up: SendEscape("[A"); e.Handled = true; break;
            case Key.Down: SendEscape("[B"); e.Handled = true; break;
            case Key.C when (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control:
                CopySelectionToClipboard(); e.Handled = true; break;
            case Key.V when (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control:
                PasteFromClipboard(); e.Handled = true; break;
        }
    }

    private void SendEscape(string seq)
    {
        if (_script == null) return;
        _script.EnqueueImmediate('\x1B');
        foreach (var c in seq) _script.EnqueueImmediate(c);
    }
    #endregion

    #region Selection
    private bool HasSelection => _selectionAnchor.HasValue && _selectionEnd.HasValue;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var pos = e.GetPosition(this);
        _selectionAnchor = _selectionEnd = ScreenFromPoint(pos);
        _isSelecting = true;
        CaptureMouse();
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_isSelecting)
        {
            var pos = e.GetPosition(this);
            _selectionEnd = ScreenFromPoint(pos);
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_isSelecting)
        {
            _isSelecting = false;
            ReleaseMouseCapture();
            InvalidateVisual();
        }
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (HasSelection)
        {
            CopySelectionToClipboard();
        }
    }

    private Point ScreenFromPoint(Point p)
    {
        int x = (int)Math.Floor((p.X - 2) / _charWidth);
        int y = (int)Math.Floor((p.Y - 2) / _charHeight);
        if (_buffer != null)
        {
            x = Math.Clamp(x, 0, _buffer.Columns - 1);
            y = Math.Clamp(y, 0, _buffer.Rows - 1);
        }
        return new Point(x, y);
    }

    private void CopySelectionToClipboard()
    {
        if (_buffer == null || !HasSelection) return;
        var (ax, ay) = ((int)_selectionAnchor!.Value.X, (int)_selectionAnchor!.Value.Y);
        var (bx, by) = ((int)_selectionEnd!.Value.X, (int)_selectionEnd!.Value.Y);
        if (ay > by || (ay == by && ax > bx)) { (ax, bx) = (bx, ax); (ay, by) = (by, ay); }
        var sb = new StringBuilder();
        for (int y = ay; y <= by; y++)
        {
            for (int x = (y == ay ? ax : 0); x <= (y == by ? bx : (_buffer.Columns - 1)); x++)
            {
                sb.Append(_buffer.GetChar(x, y));
            }
            if (y < by) sb.Append('\n');
        }
        Clipboard.SetText(sb.ToString());
    }

    private void PasteFromClipboard()
    {
        if (_script == null || _scrollOffset != 0) return;
        if (!Clipboard.ContainsText()) return;
        var text = Clipboard.GetText();
        if (string.IsNullOrEmpty(text)) return;
        // Normalize line endings to CR
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        foreach (var ch in text)
        {
            if (ch == '\n')
            {
                _script.EnqueueImmediate('\r');
            }
            else if (!char.IsControl(ch) || ch == '\t')
            {
                _script.EnqueueImmediate(ch);
            }
        }
    }
    #endregion

    #region Resize
    private void UpdateTerminalGeometry()
    {
        if (_buffer == null) return;
        if (_charWidth <= 0 || _charHeight <= 0) return;
        int cols = Math.Max(10, (int)((ActualWidth - 4) / _charWidth));
        int rows = Math.Max(5, (int)((ActualHeight - 4) / _charHeight));
        if (cols != _buffer.Columns || rows != _buffer.Rows)
        {
            _buffer.Resize(cols, rows);
            SendNaws(cols, rows);
        }
        else
        {
            // Even if size is same, user might expect renegotiation (F5 equivalent) when font size changes drastically
            // Allow manual trigger by setting TerminalFontSize again or provide public method ForceResizeNegotiation.
        }
    }

    private void SendNaws(int cols, int rows)
    {
        if (_telnet == null) return;
        var now = DateTime.UtcNow;
        if (now - _lastNaws < NawsThrottle)
        {
            return;
        }
        _lastNaws = now;
        _telnet.NotifyResize(cols, rows);
    }

    /// <summary>
    /// Public method to force NAWS negotiation (acts like legacy F5 refresh).
    /// </summary>
    public void ForceResizeNegotiation()
    {
        if (_buffer == null) return;
        SendNaws(_buffer.Columns, _buffer.Rows);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdateTerminalGeometry();
    }
    #endregion
}
