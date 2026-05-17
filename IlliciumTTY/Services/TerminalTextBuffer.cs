using System;
using System.Collections.Generic;
using System.Text;

namespace IlliciumTTY.Services;

internal readonly record struct TerminalTextSnapshot(
    string Text,
    int CursorIndex,
    IReadOnlyList<TerminalStyleSpan> StyleSpans)
{
    public static TerminalTextSnapshot Empty { get; } = new(string.Empty, 0, []);
}

internal readonly record struct TerminalStyleSpan(
    int Start,
    int Length,
    int? ForegroundColor,
    int? BackgroundColor,
    bool Inverse);

internal readonly record struct TerminalCellStyle(int? ForegroundColor, int? BackgroundColor, bool Inverse)
{
    public static TerminalCellStyle Default { get; } = new(null, null, false);
}

internal readonly record struct TerminalCell(char Character, TerminalCellStyle Style)
{
    public static TerminalCell Blank(TerminalCellStyle style) => new(' ', style);
}

internal sealed class TerminalTextBuffer(int maxLines)
{
    private const char Escape = '\u001b';
    private const int DefaultColumns = 80;
    private const int DefaultRows = 24;

    private readonly StringBuilder _controlBuffer = new();
    private readonly StringBuilder _oscBuffer = new();
    private readonly List<string> _scrollback = [];
    private TerminalCell[,] _alternateScreen = CreateScreen(DefaultRows, DefaultColumns);
    private int _columns = DefaultColumns;
    private TerminalCellStyle _currentStyle = TerminalCellStyle.Default;
    private int _cursorColumn;
    private int _cursorRow;
    private char _lastPrintedCharacter = ' ';
    private TerminalCell[,] _primaryScreen = CreateScreen(DefaultRows, DefaultColumns);
    private int _rows = DefaultRows;
    private int _savedColumn;
    private int _savedPrimaryColumn;
    private int _savedPrimaryRow;
    private int _savedRow;
    private int _scrollBottom = DefaultRows - 1;
    private int _scrollTop;
    private EscapeState _state;
    private bool _useAlternateScreen;

    public string Text
    {
        get { return Snapshot.Text; }
    }

    public TerminalTextSnapshot Snapshot =>
        _useAlternateScreen
            ? RenderScreenSnapshot(CurrentScreen, trimTrailingEmptyRows: false)
            : RenderPrimarySnapshot();

    private TerminalCell[,] CurrentScreen => _useAlternateScreen ? _alternateScreen : _primaryScreen;

    public void Clear()
    {
        _scrollback.Clear();
        ClearScreen(_primaryScreen);
        ClearScreen(_alternateScreen);
        _controlBuffer.Clear();
        _oscBuffer.Clear();
        _cursorRow = 0;
        _cursorColumn = 0;
        _savedRow = 0;
        _savedColumn = 0;
        _savedPrimaryRow = 0;
        _savedPrimaryColumn = 0;
        _scrollTop = 0;
        _scrollBottom = _rows - 1;
        _useAlternateScreen = false;
        _state = EscapeState.None;
        _lastPrintedCharacter = ' ';
        _currentStyle = TerminalCellStyle.Default;
    }

    public void Resize(int columns, int rows)
    {
        columns = Math.Clamp(columns, 20, 300);
        rows = Math.Clamp(rows, 5, 120);

        if (columns == _columns && rows == _rows)
        {
            return;
        }

        _primaryScreen = ResizeScreen(_primaryScreen, _rows, _columns, rows, columns);
        _alternateScreen = ResizeScreen(_alternateScreen, _rows, _columns, rows, columns);
        _columns = columns;
        _rows = rows;
        _cursorRow = Math.Clamp(_cursorRow, 0, _rows - 1);
        _cursorColumn = Math.Clamp(_cursorColumn, 0, _columns - 1);
        _savedRow = Math.Clamp(_savedRow, 0, _rows - 1);
        _savedColumn = Math.Clamp(_savedColumn, 0, _columns - 1);
        _savedPrimaryRow = Math.Clamp(_savedPrimaryRow, 0, _rows - 1);
        _savedPrimaryColumn = Math.Clamp(_savedPrimaryColumn, 0, _columns - 1);
        _scrollTop = 0;
        _scrollBottom = _rows - 1;
    }

    public string? Append(string data)
    {
        string? currentPath = null;

        foreach (var character in data)
        {
            var detectedPath = Append(character);
            if (!string.IsNullOrWhiteSpace(detectedPath))
            {
                currentPath = detectedPath;
            }
        }

        return currentPath;
    }

    private TerminalTextSnapshot RenderPrimarySnapshot()
    {
        var screenSnapshot = RenderScreenSnapshot(_primaryScreen, trimTrailingEmptyRows: true);
        if (_scrollback.Count == 0)
        {
            return screenSnapshot;
        }

        var builder = new StringBuilder();
        var spans = new List<TerminalStyleSpan>();
        foreach (var line in _scrollback)
        {
            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            var start = builder.Length;
            builder.Append(line);
            AppendStyleSpan(spans, start, line.Length, TerminalCellStyle.Default);
        }

        if (string.IsNullOrEmpty(screenSnapshot.Text))
        {
            builder.Append('\n');
            return new TerminalTextSnapshot(builder.ToString(), builder.Length, spans);
        }

        builder.Append('\n');
        var screenStart = builder.Length;
        builder.Append(screenSnapshot.Text);
        foreach (var span in screenSnapshot.StyleSpans)
        {
            spans.Add(span with { Start = screenStart + span.Start });
        }

        return new TerminalTextSnapshot(builder.ToString(), screenStart + screenSnapshot.CursorIndex, spans);
    }

    private TerminalTextSnapshot RenderScreenSnapshot(TerminalCell[,] screen, bool trimTrailingEmptyRows)
    {
        var lastRow = _rows - 1;
        var cursorRow = ClampRow(_cursorRow);
        var cursorColumn = Math.Clamp(_cursorColumn, 0, _columns);

        if (trimTrailingEmptyRows)
        {
            while (lastRow >= 0 && lastRow != cursorRow && IsBlankLine(screen, lastRow))
            {
                lastRow--;
            }
        }

        lastRow = Math.Max(lastRow, cursorRow);
        if (lastRow < 0)
        {
            return TerminalTextSnapshot.Empty;
        }

        var builder = new StringBuilder();
        var spans = new List<TerminalStyleSpan>();
        var cursorIndex = 0;
        for (var row = 0; row <= lastRow; row++)
        {
            if (row > 0)
            {
                builder.Append('\n');
            }

            var lineStart = builder.Length;
            AppendRenderedLine(builder, spans, screen, row, row == cursorRow ? cursorColumn : 0);
            if (row == cursorRow)
            {
                cursorIndex = lineStart + cursorColumn;
            }
        }

        return new TerminalTextSnapshot(
            builder.ToString(),
            Math.Clamp(cursorIndex, 0, builder.Length),
            spans);
    }

    private string? Append(char character)
    {
        switch (_state)
        {
            case EscapeState.None:
                return AppendNormal(character);

            case EscapeState.Escape:
                return AppendEscape(character);

            case EscapeState.Csi:
                return AppendControlSequence(character);

            case EscapeState.Osc:
                return AppendOsc(character);

            case EscapeState.OscEscape:
                return AppendOscEscape(character);

            case EscapeState.CharacterSet:
                _state = EscapeState.None;
                return null;

            default:
                _state = EscapeState.None;
                return null;
        }
    }

    private string? AppendNormal(char character)
    {
        switch (character)
        {
            case Escape:
                _state = EscapeState.Escape;
                return null;

            case '\0':
            case '\a':
                return null;

            case '\r':
                _cursorColumn = 0;
                return null;

            case '\n':
            case '\v':
            case '\f':
                LineFeed();
                return null;

            case '\b':
                if (_cursorColumn > 0)
                {
                    _cursorColumn--;
                }

                return null;

            case '\t':
                _cursorColumn = Math.Min((_cursorColumn / 8 + 1) * 8, _columns - 1);
                return null;

            case '\u007f':
                return null;

            default:
                if (!char.IsControl(character))
                {
                    PrintCharacter(character);
                }

                return null;
        }
    }

    private string? AppendEscape(char character)
    {
        switch (character)
        {
            case '[':
                _controlBuffer.Clear();
                _state = EscapeState.Csi;
                break;

            case ']':
                _oscBuffer.Clear();
                _state = EscapeState.Osc;
                break;

            case '(':
            case ')':
            case '*':
            case '+':
                _state = EscapeState.CharacterSet;
                break;

            case '7':
                SaveCursor();
                _state = EscapeState.None;
                break;

            case '8':
                RestoreCursor();
                _state = EscapeState.None;
                break;

            case 'D':
                LineFeed();
                _state = EscapeState.None;
                break;

            case 'E':
                _cursorColumn = 0;
                LineFeed();
                _state = EscapeState.None;
                break;

            case 'M':
                ReverseIndex();
                _state = EscapeState.None;
                break;

            case 'c':
                Clear();
                _state = EscapeState.None;
                break;

            default:
                _state = EscapeState.None;
                break;
        }

        return null;
    }

    private string? AppendControlSequence(char character)
    {
        _controlBuffer.Append(character);
        if (character is < '@' or > '~')
        {
            return null;
        }

        HandleControlSequence(_controlBuffer.ToString());
        _controlBuffer.Clear();
        _state = EscapeState.None;
        return null;
    }

    private string? AppendOsc(char character)
    {
        switch (character)
        {
            case '\a':
                _state = EscapeState.None;
                return ExtractPathFromOsc(_oscBuffer.ToString());

            case Escape:
                _state = EscapeState.OscEscape;
                return null;

            default:
                _oscBuffer.Append(character);
                return null;
        }
    }

    private string? AppendOscEscape(char character)
    {
        if (character == '\\')
        {
            _state = EscapeState.None;
            return ExtractPathFromOsc(_oscBuffer.ToString());
        }

        _oscBuffer.Append(Escape);
        _oscBuffer.Append(character);
        _state = EscapeState.Osc;
        return null;
    }

    private void HandleControlSequence(string sequence)
    {
        if (sequence.Length == 0)
        {
            return;
        }

        var command = sequence[^1];
        var parameters = sequence[..^1];
        var privateMode = parameters.Length > 0 && parameters[0] == '?';
        if (privateMode)
        {
            parameters = parameters[1..];
        }

        var values = ParseParameters(parameters);

        switch (command)
        {
            case '@':
                InsertCharacters(GetParameter(values, 0, 1));
                break;

            case 'A':
                MoveCursorRelative(-GetParameter(values, 0, 1), 0);
                break;

            case 'B':
            case 'e':
                MoveCursorRelative(GetParameter(values, 0, 1), 0);
                break;

            case 'C':
            case 'a':
                MoveCursorRelative(0, GetParameter(values, 0, 1));
                break;

            case 'D':
                MoveCursorRelative(0, -GetParameter(values, 0, 1));
                break;

            case 'E':
                MoveCursorRelative(GetParameter(values, 0, 1), -_columns);
                break;

            case 'F':
                MoveCursorRelative(-GetParameter(values, 0, 1), -_columns);
                break;

            case 'G':
            case '`':
                _cursorColumn = ClampColumn(GetParameter(values, 0, 1) - 1);
                break;

            case 'H':
            case 'f':
                SetCursor(GetParameter(values, 0, 1) - 1, GetParameter(values, 1, 1) - 1);
                break;

            case 'I':
                _cursorColumn = Math.Min(_cursorColumn + GetParameter(values, 0, 1) * 8, _columns - 1);
                break;

            case 'J':
                EraseInDisplay(GetParameter(values, 0, 0));
                break;

            case 'K':
                EraseInLine(GetParameter(values, 0, 0));
                break;

            case 'L':
                InsertLines(GetParameter(values, 0, 1));
                break;

            case 'M':
                DeleteLines(GetParameter(values, 0, 1));
                break;

            case 'P':
                DeleteCharacters(GetParameter(values, 0, 1));
                break;

            case 'S':
                ScrollUp(_scrollTop, _scrollBottom, GetParameter(values, 0, 1));
                break;

            case 'T':
                ScrollDown(_scrollTop, _scrollBottom, GetParameter(values, 0, 1));
                break;

            case 'X':
                EraseCharacters(GetParameter(values, 0, 1));
                break;

            case 'b':
                RepeatLastCharacter(GetParameter(values, 0, 1));
                break;

            case 'd':
                _cursorRow = ClampRow(GetParameter(values, 0, 1) - 1);
                break;

            case 'h':
                SetMode(values, privateMode, enabled: true);
                break;

            case 'l':
                SetMode(values, privateMode, enabled: false);
                break;

            case 'm':
                ApplySgr(values);
                break;

            case 'r':
                SetScrollRegion(values);
                break;

            case 's':
                SaveCursor();
                break;

            case 'u':
                RestoreCursor();
                break;
        }
    }

    private void SetMode(IReadOnlyList<int?> values, bool privateMode, bool enabled)
    {
        if (!privateMode)
        {
            return;
        }

        foreach (var value in values)
        {
            switch (value)
            {
                case 47:
                case 1047:
                case 1049:
                    if (enabled)
                    {
                        EnterAlternateScreen(value == 1049);
                    }
                    else
                    {
                        LeaveAlternateScreen(value == 1049);
                    }

                    break;
            }
        }
    }

    private void ApplySgr(IReadOnlyList<int?> values)
    {
        if (values.Count == 0)
        {
            _currentStyle = TerminalCellStyle.Default;
            return;
        }

        for (var index = 0; index < values.Count; index++)
        {
            var value = values[index] ?? 0;
            switch (value)
            {
                case 0:
                    _currentStyle = TerminalCellStyle.Default;
                    break;

                case 7:
                    _currentStyle = _currentStyle with { Inverse = true };
                    break;

                case 27:
                    _currentStyle = _currentStyle with { Inverse = false };
                    break;

                case 30:
                case 31:
                case 32:
                case 33:
                case 34:
                case 35:
                case 36:
                case 37:
                    _currentStyle = _currentStyle with { ForegroundColor = GetAnsiColor(value - 30, bright: false) };
                    break;

                case 39:
                    _currentStyle = _currentStyle with { ForegroundColor = null };
                    break;

                case 40:
                case 41:
                case 42:
                case 43:
                case 44:
                case 45:
                case 46:
                case 47:
                    _currentStyle = _currentStyle with { BackgroundColor = GetAnsiColor(value - 40, bright: false) };
                    break;

                case 49:
                    _currentStyle = _currentStyle with { BackgroundColor = null };
                    break;

                case >= 90 and <= 97:
                    _currentStyle = _currentStyle with { ForegroundColor = GetAnsiColor(value - 90, bright: true) };
                    break;

                case >= 100 and <= 107:
                    _currentStyle = _currentStyle with { BackgroundColor = GetAnsiColor(value - 100, bright: true) };
                    break;

                case 38:
                    if (TryReadExtendedColor(values, ref index, out var foregroundColor))
                    {
                        _currentStyle = _currentStyle with { ForegroundColor = foregroundColor };
                    }

                    break;

                case 48:
                    if (TryReadExtendedColor(values, ref index, out var backgroundColor))
                    {
                        _currentStyle = _currentStyle with { BackgroundColor = backgroundColor };
                    }

                    break;
            }
        }
    }

    private void EnterAlternateScreen(bool saveCursor)
    {
        if (saveCursor)
        {
            _savedPrimaryRow = _cursorRow;
            _savedPrimaryColumn = ClampColumn(_cursorColumn);
        }

        _useAlternateScreen = true;
        ClearScreen(_alternateScreen);
        _cursorRow = 0;
        _cursorColumn = 0;
        ResetScrollRegion();
    }

    private void LeaveAlternateScreen(bool restoreCursor)
    {
        _useAlternateScreen = false;
        if (restoreCursor)
        {
            _cursorRow = _savedPrimaryRow;
            _cursorColumn = _savedPrimaryColumn;
        }

        ResetScrollRegion();
    }

    private void PrintCharacter(char character)
    {
        if (_cursorColumn >= _columns)
        {
            _cursorColumn = 0;
            LineFeed();
        }

        CurrentScreen[_cursorRow, _cursorColumn] = new TerminalCell(character, _currentStyle);
        _lastPrintedCharacter = character;
        _cursorColumn++;
    }

    private void LineFeed()
    {
        if (_cursorRow == _scrollBottom)
        {
            ScrollUp(_scrollTop, _scrollBottom, 1);
            _cursorColumn = Math.Min(_cursorColumn, _columns - 1);
            return;
        }

        _cursorRow = ClampRow(_cursorRow + 1);
        _cursorColumn = Math.Min(_cursorColumn, _columns - 1);
    }

    private void ReverseIndex()
    {
        if (_cursorRow == _scrollTop)
        {
            ScrollDown(_scrollTop, _scrollBottom, 1);
            return;
        }

        _cursorRow = ClampRow(_cursorRow - 1);
    }

    private void SaveCursor()
    {
        _savedRow = _cursorRow;
        _savedColumn = ClampColumn(_cursorColumn);
    }

    private void RestoreCursor()
    {
        _cursorRow = ClampRow(_savedRow);
        _cursorColumn = ClampColumn(_savedColumn);
    }

    private void SetCursor(int row, int column)
    {
        _cursorRow = ClampRow(row);
        _cursorColumn = ClampColumn(column);
    }

    private void MoveCursorRelative(int rowDelta, int columnDelta)
    {
        _cursorRow = ClampRow(_cursorRow + rowDelta);
        _cursorColumn = ClampColumn(_cursorColumn + columnDelta);
    }

    private void SetScrollRegion(IReadOnlyList<int?> values)
    {
        var top = GetParameter(values, 0, 1) - 1;
        var bottom = GetParameter(values, 1, _rows) - 1;
        if (top < 0 || bottom <= top || bottom >= _rows)
        {
            ResetScrollRegion();
            return;
        }

        _scrollTop = top;
        _scrollBottom = bottom;
        SetCursor(0, 0);
    }

    private void ResetScrollRegion()
    {
        _scrollTop = 0;
        _scrollBottom = _rows - 1;
    }

    private void EraseInDisplay(int mode)
    {
        var screen = CurrentScreen;

        switch (mode)
        {
            case 0:
                ClearRange(screen, _cursorRow, _cursorColumn, _rows - 1, _columns - 1, _currentStyle);
                break;

            case 1:
                ClearRange(screen, 0, 0, _cursorRow, _cursorColumn, _currentStyle);
                break;

            case 2:
                ClearScreen(screen, _currentStyle);
                break;

            case 3:
                if (!_useAlternateScreen)
                {
                    _scrollback.Clear();
                }

                ClearScreen(screen, _currentStyle);
                break;
        }
    }

    private void EraseInLine(int mode)
    {
        var screen = CurrentScreen;
        var column = ClampColumn(_cursorColumn);

        switch (mode)
        {
            case 0:
                ClearLineRange(screen, _cursorRow, column, _columns - 1, _currentStyle);
                break;

            case 1:
                ClearLineRange(screen, _cursorRow, 0, column, _currentStyle);
                break;

            case 2:
                ClearLineRange(screen, _cursorRow, 0, _columns - 1, _currentStyle);
                break;
        }
    }

    private void InsertLines(int count)
    {
        if (_cursorRow < _scrollTop || _cursorRow > _scrollBottom)
        {
            return;
        }

        ScrollDown(_cursorRow, _scrollBottom, count);
    }

    private void DeleteLines(int count)
    {
        if (_cursorRow < _scrollTop || _cursorRow > _scrollBottom)
        {
            return;
        }

        ScrollUp(_cursorRow, _scrollBottom, count);
    }

    private void InsertCharacters(int count)
    {
        count = Math.Clamp(count, 1, _columns);
        var screen = CurrentScreen;
        var column = ClampColumn(_cursorColumn);
        for (var target = _columns - 1; target >= column + count; target--)
        {
            screen[_cursorRow, target] = screen[_cursorRow, target - count];
        }

        ClearLineRange(screen, _cursorRow, column, Math.Min(column + count - 1, _columns - 1), _currentStyle);
    }

    private void DeleteCharacters(int count)
    {
        count = Math.Clamp(count, 1, _columns);
        var screen = CurrentScreen;
        var column = ClampColumn(_cursorColumn);
        for (var target = column; target < _columns - count; target++)
        {
            screen[_cursorRow, target] = screen[_cursorRow, target + count];
        }

        ClearLineRange(screen, _cursorRow, Math.Max(_columns - count, column), _columns - 1, _currentStyle);
    }

    private void EraseCharacters(int count)
    {
        count = Math.Clamp(count, 1, _columns);
        var column = ClampColumn(_cursorColumn);
        ClearLineRange(CurrentScreen, _cursorRow, column, Math.Min(column + count - 1, _columns - 1), _currentStyle);
    }

    private void RepeatLastCharacter(int count)
    {
        count = Math.Clamp(count, 1, _columns);
        for (var i = 0; i < count; i++)
        {
            PrintCharacter(_lastPrintedCharacter);
        }
    }

    private void ScrollUp(int top, int bottom, int count)
    {
        if (top < 0 || bottom >= _rows || top > bottom)
        {
            return;
        }

        count = Math.Clamp(count, 1, bottom - top + 1);
        var screen = CurrentScreen;

        for (var i = 0; i < count; i++)
        {
            if (!_useAlternateScreen && top == 0 && bottom == _rows - 1)
            {
                AddScrollbackLine(RenderLine(_primaryScreen, 0));
            }

            for (var row = top; row < bottom; row++)
            {
                CopyLine(screen, row + 1, row);
            }

            ClearLine(screen, bottom, _currentStyle);
        }
    }

    private void ScrollDown(int top, int bottom, int count)
    {
        if (top < 0 || bottom >= _rows || top > bottom)
        {
            return;
        }

        count = Math.Clamp(count, 1, bottom - top + 1);
        var screen = CurrentScreen;
        for (var i = 0; i < count; i++)
        {
            for (var row = bottom; row > top; row--)
            {
                CopyLine(screen, row - 1, row);
            }

            ClearLine(screen, top, _currentStyle);
        }
    }

    private void AddScrollbackLine(string line)
    {
        _scrollback.Add(line);
        while (_scrollback.Count > maxLines)
        {
            _scrollback.RemoveAt(0);
        }
    }

    private static bool TryReadExtendedColor(IReadOnlyList<int?> values, ref int index, out int color)
    {
        color = 0;
        if (index + 1 >= values.Count)
        {
            return false;
        }

        var mode = values[++index] ?? 0;
        if (mode == 5 && index + 1 < values.Count)
        {
            color = GetXtermColor(values[++index] ?? 0);
            return true;
        }

        if (mode == 2 && index + 3 < values.Count)
        {
            var red = Math.Clamp(values[++index] ?? 0, 0, 255);
            var green = Math.Clamp(values[++index] ?? 0, 0, 255);
            var blue = Math.Clamp(values[++index] ?? 0, 0, 255);
            color = PackColor(red, green, blue);
            return true;
        }

        return false;
    }

    private static int GetAnsiColor(int index, bool bright)
    {
        return (index, bright) switch
        {
            (0, false) => 0x0B1020,
            (1, false) => 0xEF4444,
            (2, false) => 0x22C55E,
            (3, false) => 0xEAB308,
            (4, false) => 0x3B82F6,
            (5, false) => 0xA855F7,
            (6, false) => 0x06B6D4,
            (7, false) => 0xE5E7EB,
            (0, true) => 0x64748B,
            (1, true) => 0xF87171,
            (2, true) => 0x4ADE80,
            (3, true) => 0xFACC15,
            (4, true) => 0x60A5FA,
            (5, true) => 0xC084FC,
            (6, true) => 0x22D3EE,
            (7, true) => 0xF8FAFC,
            _ => 0xDDE7F5
        };
    }

    private static int GetXtermColor(int index)
    {
        index = Math.Clamp(index, 0, 255);
        if (index < 16)
        {
            return GetAnsiColor(index % 8, index >= 8);
        }

        if (index < 232)
        {
            var cubeIndex = index - 16;
            var red = cubeIndex / 36;
            var green = cubeIndex / 6 % 6;
            var blue = cubeIndex % 6;
            return PackColor(ToXtermCubeChannel(red), ToXtermCubeChannel(green), ToXtermCubeChannel(blue));
        }

        var gray = 8 + (index - 232) * 10;
        return PackColor(gray, gray, gray);
    }

    private static int ToXtermCubeChannel(int value)
    {
        return value == 0 ? 0 : 55 + value * 40;
    }

    private static int PackColor(int red, int green, int blue)
    {
        return red << 16 | green << 8 | blue;
    }

    private string? ExtractPathFromOsc(string sequence)
    {
        const string prefix = "7;file://";
        if (!sequence.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var value = sequence[prefix.Length..];
        var pathStart = value.IndexOf('/');
        return pathStart < 0
            ? null
            : Uri.UnescapeDataString(value[pathStart..]);
    }

    private static List<int?> ParseParameters(string parameters)
    {
        var values = new List<int?>();
        if (parameters.Length == 0)
        {
            return values;
        }

        foreach (var rawPart in parameters.Split(';'))
        {
            var part = rawPart;
            var colonIndex = part.IndexOf(':');
            if (colonIndex >= 0)
            {
                part = part[..colonIndex];
            }

            values.Add(int.TryParse(part, out var value) ? value : null);
        }

        return values;
    }

    private static int GetParameter(IReadOnlyList<int?> values, int index, int defaultValue)
    {
        if (index >= values.Count)
        {
            return defaultValue;
        }

        var value = values[index] ?? defaultValue;
        return value == 0 ? defaultValue : value;
    }

    private static TerminalCell[,] CreateScreen(int rows, int columns)
    {
        var screen = new TerminalCell[rows, columns];
        ClearScreen(screen);
        return screen;
    }

    private static TerminalCell[,] ResizeScreen(
        TerminalCell[,] oldScreen,
        int oldRows,
        int oldColumns,
        int newRows,
        int newColumns)
    {
        var newScreen = CreateScreen(newRows, newColumns);
        var rowsToCopy = Math.Min(oldRows, newRows);
        var columnsToCopy = Math.Min(oldColumns, newColumns);

        for (var row = 0; row < rowsToCopy; row++)
        {
            for (var column = 0; column < columnsToCopy; column++)
            {
                newScreen[row, column] = oldScreen[row, column];
            }
        }

        return newScreen;
    }

    private static void ClearScreen(TerminalCell[,] screen, TerminalCellStyle? style = null)
    {
        var blankStyle = style ?? TerminalCellStyle.Default;
        for (var row = 0; row < screen.GetLength(0); row++)
        {
            ClearLine(screen, row, blankStyle);
        }
    }

    private static void ClearLine(TerminalCell[,] screen, int row, TerminalCellStyle? style = null)
    {
        var blank = TerminalCell.Blank(style ?? TerminalCellStyle.Default);
        for (var column = 0; column < screen.GetLength(1); column++)
        {
            screen[row, column] = blank;
        }
    }

    private static void ClearLineRange(
        TerminalCell[,] screen,
        int row,
        int startColumn,
        int endColumn,
        TerminalCellStyle? style = null)
    {
        if (row < 0 || row >= screen.GetLength(0))
        {
            return;
        }

        var firstColumn = Math.Clamp(startColumn, 0, screen.GetLength(1) - 1);
        var lastColumn = Math.Clamp(endColumn, 0, screen.GetLength(1) - 1);
        var blank = TerminalCell.Blank(style ?? TerminalCellStyle.Default);
        for (var column = firstColumn; column <= lastColumn; column++)
        {
            screen[row, column] = blank;
        }
    }

    private static void ClearRange(
        TerminalCell[,] screen,
        int startRow,
        int startColumn,
        int endRow,
        int endColumn,
        TerminalCellStyle? style = null)
    {
        var firstRow = Math.Clamp(startRow, 0, screen.GetLength(0) - 1);
        var lastRow = Math.Clamp(endRow, 0, screen.GetLength(0) - 1);

        for (var row = firstRow; row <= lastRow; row++)
        {
            var firstColumn = row == firstRow ? startColumn : 0;
            var lastColumn = row == lastRow ? endColumn : screen.GetLength(1) - 1;
            ClearLineRange(screen, row, firstColumn, lastColumn, style);
        }
    }

    private static void CopyLine(TerminalCell[,] screen, int sourceRow, int targetRow)
    {
        for (var column = 0; column < screen.GetLength(1); column++)
        {
            screen[targetRow, column] = screen[sourceRow, column];
        }
    }

    private static void AppendRenderedLine(
        StringBuilder builder,
        List<TerminalStyleSpan> spans,
        TerminalCell[,] screen,
        int row,
        int minimumLength = 0)
    {
        var length = GetRenderedLineLength(screen, row, minimumLength);
        if (length <= 0)
        {
            return;
        }

        var runStart = builder.Length;
        var runStyle = screen[row, 0].Style;
        for (var column = 0; column < length; column++)
        {
            var cell = screen[row, column];
            if (cell.Style != runStyle)
            {
                AppendStyleSpan(spans, runStart, builder.Length - runStart, runStyle);
                runStart = builder.Length;
                runStyle = cell.Style;
            }

            builder.Append(cell.Character);
        }

        AppendStyleSpan(spans, runStart, builder.Length - runStart, runStyle);
    }

    private static void AppendStyleSpan(
        List<TerminalStyleSpan> spans,
        int start,
        int length,
        TerminalCellStyle style)
    {
        if (length <= 0)
        {
            return;
        }

        spans.Add(new TerminalStyleSpan(
            start,
            length,
            style.ForegroundColor,
            style.BackgroundColor,
            style.Inverse));
    }

    private static string RenderLine(TerminalCell[,] screen, int row, int minimumLength = 0)
    {
        var length = GetRenderedLineLength(screen, row, minimumLength);
        return length <= 0
            ? string.Empty
            : new string(GetLineCharacters(screen, row, length));
    }

    private static int GetRenderedLineLength(TerminalCell[,] screen, int row, int minimumLength)
    {
        var lastColumn = screen.GetLength(1) - 1;
        while (lastColumn >= 0 &&
               screen[row, lastColumn].Character == ' ' &&
               screen[row, lastColumn].Style == TerminalCellStyle.Default)
        {
            lastColumn--;
        }

        return Math.Max(lastColumn + 1, minimumLength);
    }

    private static char[] GetLineCharacters(TerminalCell[,] screen, int row, int length)
    {
        var characters = new char[length];
        for (var column = 0; column < length; column++)
        {
            characters[column] = screen[row, column].Character;
        }

        return characters;
    }

    private static bool IsBlankLine(TerminalCell[,] screen, int row)
    {
        for (var column = 0; column < screen.GetLength(1); column++)
        {
            if (screen[row, column].Character != ' ' ||
                screen[row, column].Style != TerminalCellStyle.Default)
            {
                return false;
            }
        }

        return true;
    }

    private int ClampRow(int row)
    {
        return Math.Clamp(row, 0, _rows - 1);
    }

    private int ClampColumn(int column)
    {
        return Math.Clamp(column, 0, _columns - 1);
    }

    private enum EscapeState
    {
        None,
        Escape,
        Csi,
        Osc,
        OscEscape,
        CharacterSet
    }
}