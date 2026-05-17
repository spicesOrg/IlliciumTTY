using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;
using IlliciumTTY.Models;
using IlliciumTTY.Services;
using IlliciumTTY.ViewModels;
using AvaloniaKey = Avalonia.Input.Key;
using AvaloniaKeyModifiers = Avalonia.Input.KeyModifiers;

namespace IlliciumTTY.Controls;

public partial class TerminalPane : UserControl
{
    private const int DefaultTerminalBackgroundColor = 0x0B1020;
    private const int DefaultTerminalForegroundColor = 0xDDE7F5;
    private const uint TerminalCellPixelHeight = 16;
    private const uint TerminalCellPixelWidth = 8;

    public static readonly StyledProperty<bool> IsPreviewProperty =
        AvaloniaProperty.Register<TerminalPane, bool>(nameof(IsPreview));

    private bool _isDisposing;
    private bool _isTerminalScrollViewerSubscribed;
    private TerminalTextSnapshot _lastSnapshot = TerminalTextSnapshot.Empty;
    private TerminalSessionViewModel? _session;
    private int _terminalRenderVersion;
    private ScrollViewer? _terminalScrollViewer;
    private TextPresenter? _terminalTextPresenter;

    public TerminalPane()
    {
        InitializeComponent();
        ApplyPreviewMode();
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        Loaded += OnLoaded;
        TerminalText.PropertyChanged += OnTerminalTextPropertyChanged;
        TerminalText.AddHandler(KeyDownEvent, OnTerminalKeyDown, RoutingStrategies.Tunnel);
        TerminalText.AddHandler(TextInputEvent, OnTerminalTextInput, RoutingStrategies.Tunnel);
        TerminalText.AddHandler(PointerPressedEvent, OnTerminalPointerPressed, RoutingStrategies.Tunnel);
    }

    public bool IsPreview
    {
        get => GetValue(IsPreviewProperty);
        set => SetValue(IsPreviewProperty, value);
    }

    private bool HasTerminalSelection => !string.IsNullOrEmpty(TerminalText.SelectedText);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsPreviewProperty)
        {
            ApplyPreviewMode();
        }
    }

    private void ApplyPreviewMode()
    {
        TerminalSurface.IsHitTestVisible = !IsPreview;
        TerminalStyleLayer.IsVisible = true;
        TerminalText.Focusable = !IsPreview;
        TerminalText.IsTabStop = !IsPreview;
        TerminalText.FontSize = IsPreview ? 11 : 13;
        TerminalText.Padding = IsPreview ? new Thickness(6) : new Thickness(8);
        TerminalText.ContextMenu = IsPreview ? null : TerminalContextMenu;
        TerminalOverlay.Padding = IsPreview ? new Thickness(10) : new Thickness(24);
        TerminalOverlayContent.Spacing = IsPreview ? 5 : 12;
        OverlayName.FontSize = IsPreview ? 13 : 18;
        OverlayHost.FontSize = IsPreview ? 11 : 12;
        OverlayStatus.FontSize = IsPreview ? 11 : 12;
        OverlayLastError.FontSize = IsPreview ? 11 : 12;
        OverlayConnectionPreview.IsVisible = !IsPreview;
        OverlayActions.IsVisible = !IsPreview;
        UpdateTerminalPresentation(_session?.TerminalSnapshot ?? TerminalTextSnapshot.Empty);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        BindSession(DataContext as TerminalSessionViewModel);
    }

    private void BindSession(TerminalSessionViewModel? session)
    {
        if (ReferenceEquals(_session, session))
        {
            TryLaunchIfRequested();
            return;
        }

        if (_session is not null)
        {
            DetachSession(_session);
        }

        _session = session;

        if (_session is not null)
        {
            _session.PropertyChanged += OnSessionPropertyChanged;
            _session.TerminalTextChanged += OnSessionTerminalTextChanged;
            RenderTerminalText(_session);
            if (_session.Status == TerminalStatus.Connected)
            {
                _session.ChangeTerminalSize(GetTerminalSize());
                FocusTerminalIfInteractive();
            }

            TryLaunchIfRequested();
            SendPendingInput();
        }
        else
        {
            RenderTerminalText(null);
        }
    }

    private void DetachSession(TerminalSessionViewModel session)
    {
        session.PropertyChanged -= OnSessionPropertyChanged;
        session.TerminalTextChanged -= OnSessionTerminalTextChanged;
    }

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        if (e.PropertyName == nameof(TerminalSessionViewModel.Status))
        {
            if (_session.Status == TerminalStatus.Connecting)
            {
                TryLaunchIfRequested();
            }
            else if (_session.Status == TerminalStatus.Connected)
            {
                _session.ChangeTerminalSize(GetTerminalSize());
                RenderTerminalText(_session);
                FocusTerminalIfInteractive();
            }

            UpdateTerminalPresentation(_session.TerminalSnapshot);
        }
        else if (e.PropertyName == nameof(TerminalSessionViewModel.PendingInput))
        {
            SendPendingInput();
        }
    }

    private void TryLaunchIfRequested()
    {
        if (_session is null || _session.Status != TerminalStatus.Connecting)
        {
            return;
        }

        var terminalClient = new SshTerminalClient();
        var connectCts = new CancellationTokenSource();
        if (!_session.TryBeginTerminalConnect(terminalClient, connectCts))
        {
            connectCts.Dispose();
            terminalClient.Dispose();
            return;
        }

        _ = ConnectTerminalAsync(_session, terminalClient, connectCts);
    }

    private async Task ConnectTerminalAsync(
        TerminalSessionViewModel session,
        SshTerminalClient terminalClient,
        CancellationTokenSource connectCts)
    {
        try
        {
            var terminalSize = GetTerminalSize();
            session.ChangeTerminalSize(terminalSize);

            await terminalClient.ConnectAsync(
                session.Link.ToLinkModel(),
                terminalSize,
                connectCts.Token);

            if (connectCts.IsCancellationRequested ||
                !session.IsCurrentTerminalClient(terminalClient))
            {
                if (session.IsCurrentTerminalClient(terminalClient))
                {
                    session.DisposeTerminalClient(terminalClient);
                }
                else
                {
                    terminalClient.Dispose();
                }

                return;
            }

            session.CompleteTerminalConnect(terminalClient);
            session.MarkConnected();
            if (!_isDisposing && ReferenceEquals(_session, session))
            {
                RenderTerminalText(session);
                FocusTerminalIfInteractive();
            }

            SendInitialRemotePath(session);
            SendPendingInput(session);
        }
        catch (OperationCanceledException) when (connectCts.IsCancellationRequested)
        {
            if (session.IsCurrentTerminalClient(terminalClient))
            {
                session.DisposeTerminalClient(terminalClient);
            }
        }
        catch (Exception ex)
        {
            if (session.IsCurrentTerminalClient(terminalClient))
            {
                session.DisposeTerminalClient(terminalClient);
                session.MarkError(SshConnectionDiagnostics.ExplainException(ex));
            }
        }
        finally
        {
            connectCts.Dispose();
        }
    }

    private void SendInitialRemotePath(TerminalSessionViewModel session)
    {
        var defaultRemotePath = session.Link.DefaultRemotePath;
        if (string.IsNullOrWhiteSpace(defaultRemotePath) ||
            defaultRemotePath is "~" or ".")
        {
            return;
        }

        SendTerminalInput(session, SshCommandBuilder.BuildCdCommand(defaultRemotePath) + "\n");
        session.ReportCurrentPath(defaultRemotePath);
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _terminalTextPresenter = null;
        ResetTerminalScrollViewer();
        TryLaunchIfRequested();
        UpdateTerminalPresentation(_session?.TerminalSnapshot ?? TerminalTextSnapshot.Empty);
    }

    private void OnSessionTerminalTextChanged(object? sender, EventArgs e)
    {
        var session = sender as TerminalSessionViewModel;
        Dispatcher.UIThread.Post(() =>
        {
            if (_isDisposing || !ReferenceEquals(_session, session))
            {
                return;
            }

            RenderTerminalText(session);
        });
    }

    private void RenderTerminalText(TerminalSessionViewModel? session)
    {
        var snapshot = session?.TerminalSnapshot ?? TerminalTextSnapshot.Empty;
        _lastSnapshot = snapshot;
        var renderVersion = ++_terminalRenderVersion;
        TerminalText.Text = snapshot.Text;
        TerminalText.CaretIndex = snapshot.CursorIndex;
        TerminalText.ScrollToLine(Math.Max(TerminalText.GetLineCount() - 1, 0));
        UpdateTerminalPresentation(snapshot);
        Dispatcher.UIThread.Post(() =>
        {
            if (!_isDisposing && renderVersion == _terminalRenderVersion)
            {
                UpdateTerminalPresentation(snapshot);
            }
        }, DispatcherPriority.Render);
    }

    private void SendPendingInput()
    {
        if (_session is not null)
        {
            SendPendingInput(_session);
        }
    }

    private static void SendPendingInput(TerminalSessionViewModel session)
    {
        if (session.Status != TerminalStatus.Connected ||
            string.IsNullOrEmpty(session.PendingInput))
        {
            return;
        }

        SendTerminalInput(session, session.PendingInput);
        session.ClearPendingInput();
    }

    private void SendTerminalInput(string input)
    {
        if (_session is not null)
        {
            _session.SendUserInput(input);
        }
    }

    private static void SendTerminalInput(TerminalSessionViewModel session, string input)
    {
        session.WriteTerminalInput(input);
    }

    private void OnTerminalContextMenuOpened(object? sender, RoutedEventArgs e)
    {
        var hasSelection = HasTerminalSelection;
        var isConnected = _session?.Status == TerminalStatus.Connected;

        CopyTerminalMenuItem.IsEnabled = hasSelection;
        PasteTerminalMenuItem.IsEnabled = isConnected;
        SelectAllTerminalMenuItem.IsEnabled = !string.IsNullOrEmpty(TerminalText.Text);
        ClearTerminalSelectionMenuItem.IsEnabled = hasSelection;
        ConnectTerminalMenuItem.IsVisible = _session?.CanConnect == true;
        DisconnectTerminalMenuItem.IsVisible = _session?.CanDisconnect == true;
    }

    private async void OnTerminalCopyMenuClick(object? sender, RoutedEventArgs e)
    {
        await CopySelectionAsync();
    }

    private async void OnTerminalPasteMenuClick(object? sender, RoutedEventArgs e)
    {
        await PasteClipboardAsync();
    }

    private void OnTerminalSelectAllMenuClick(object? sender, RoutedEventArgs e)
    {
        TerminalText.SelectAll();
        TerminalText.Focus();
    }

    private void OnTerminalClearSelectionMenuClick(object? sender, RoutedEventArgs e)
    {
        ClearTerminalSelection();
        TerminalText.Focus();
    }

    private void OnTerminalConnectMenuClick(object? sender, RoutedEventArgs e)
    {
        if (_session?.CanConnect == true)
        {
            _session.BeginConnect();
        }
    }

    private void OnTerminalDisconnectMenuClick(object? sender, RoutedEventArgs e)
    {
        if (_session?.CanDisconnect == true)
        {
            _session.BeginDisconnect();
        }
    }

    private async void OnTerminalKeyDown(object? sender, KeyEventArgs e)
    {
        if (IsPreview)
        {
            return;
        }

        if (IsTerminalCopyGesture(e))
        {
            e.Handled = true;
            await CopySelectionAsync();
            return;
        }

        if (IsTerminalPasteGesture(e))
        {
            e.Handled = true;
            await PasteClipboardAsync();
            return;
        }

        if (_session?.Status != TerminalStatus.Connected)
        {
            return;
        }

        var input = TranslateKeyInput(e);
        if (input is null)
        {
            return;
        }

        e.Handled = true;
        SendTerminalInput(input);
    }

    private void OnTerminalTextInput(object? sender, TextInputEventArgs e)
    {
        if (IsPreview ||
            _session?.Status != TerminalStatus.Connected ||
            string.IsNullOrEmpty(e.Text))
        {
            return;
        }

        e.Handled = true;
        SendTerminalInput(e.Text);
    }

    private void OnTerminalPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsPreview)
        {
            return;
        }

        TerminalText.Focus();
    }

    private Task<bool> CopySelectionAsync()
    {
        if (!HasTerminalSelection)
        {
            return Task.FromResult(false);
        }

        TerminalText.Copy();
        return Task.FromResult(true);
    }

    private async Task PasteClipboardAsync()
    {
        if (IsPreview || _session?.Status != TerminalStatus.Connected)
        {
            return;
        }

        var text = await ReadClipboardTextAsync();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        SendTerminalInput(text);
        FocusTerminalIfInteractive();
    }

    private async Task<string?> ReadClipboardTextAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            return null;
        }

        var clipboardData = await clipboard.TryGetDataAsync();
        if (clipboardData is null)
        {
            return null;
        }

        foreach (var item in clipboardData.Items)
        {
            if (!item.Formats.Any(format => format.Equals(DataFormat.Text)))
            {
                continue;
            }

            if (await item.TryGetRawAsync(DataFormat.Text) is string text)
            {
                return text;
            }
        }

        return null;
    }

    private void ClearTerminalSelection()
    {
        TerminalText.ClearSelection();
    }

    private SshTerminalSize GetTerminalSize()
    {
        var width = ToPixelDimension(TerminalText.Bounds.Width);
        var height = ToPixelDimension(TerminalText.Bounds.Height);
        var columns = Math.Max((int)(width / TerminalCellPixelWidth), 80);
        var rows = Math.Max((int)(height / TerminalCellPixelHeight), 24);

        return new SshTerminalSize(
            (uint)columns,
            (uint)rows,
            Math.Max(width, ScaleCells(columns, TerminalCellPixelWidth)),
            Math.Max(height, ScaleCells(rows, TerminalCellPixelHeight)));
    }

    private void OnTerminalTextPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property.Name != nameof(Bounds) || _session?.Status != TerminalStatus.Connected)
        {
            return;
        }

        _session?.ChangeTerminalSize(GetTerminalSize());
        UpdateTerminalPresentation(_session?.TerminalSnapshot ?? TerminalTextSnapshot.Empty);
    }

    private void FocusTerminalIfInteractive()
    {
        if (!IsPreview)
        {
            TerminalText.Focus();
        }
    }

    private static uint ToPixelDimension(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 1)
        {
            return 0;
        }

        return value >= uint.MaxValue ? uint.MaxValue : (uint)Math.Round(value);
    }

    private static uint ScaleCells(int cellCount, uint pixelsPerCell)
    {
        return (uint)Math.Min(uint.MaxValue, (ulong)Math.Max(cellCount, 1) * pixelsPerCell);
    }

    private void UpdateTerminalPresentation(TerminalTextSnapshot snapshot)
    {
        UpdateTerminalStyleLayer(snapshot);
        UpdateTerminalCursor(snapshot);
    }

    private void UpdateTerminalStyleLayer(TerminalTextSnapshot snapshot)
    {
        TerminalStyleLayer.Children.Clear();
        if (snapshot.Text.Length == 0 || TerminalText.Bounds.Width <= 0 || TerminalText.Bounds.Height <= 0)
        {
            return;
        }

        var textPresenter = GetTerminalTextPresenter();
        var textLayout = textPresenter?.TextLayout;
        if (textPresenter is null || textLayout is null)
        {
            return;
        }

        GetTerminalScrollViewer();
        foreach (var span in snapshot.StyleSpans)
        {
            AddTerminalStyleSpan(snapshot.Text, span, textPresenter, textLayout);
        }
    }

    private void AddTerminalStyleSpan(
        string text,
        TerminalStyleSpan span,
        TextPresenter textPresenter,
        TextLayout textLayout)
    {
        var start = Math.Clamp(span.Start, 0, text.Length);
        var end = Math.Clamp(span.Start + span.Length, start, text.Length);
        if (end <= start)
        {
            return;
        }

        var startBounds = textLayout.HitTestTextPosition(start);
        var endBounds = textLayout.HitTestTextPosition(end);
        var startPoint = textPresenter.TranslatePoint(new Point(startBounds.X, startBounds.Y), TerminalSurface);
        if (startPoint is not { } point)
        {
            return;
        }

        var height = startBounds.Height > 0 ? startBounds.Height : TerminalCellPixelHeight;
        var width = endBounds.X > startBounds.X
            ? endBounds.X - startBounds.X
            : GetFallbackSpanWidth(textLayout, start, end, startBounds);
        if (width <= 0 || !IsCursorVisible(point, width, height))
        {
            return;
        }

        var backgroundColor = ResolveBackgroundColor(span);
        if (backgroundColor is not null)
        {
            var background = new Border
            {
                Width = width,
                Height = height,
                Background = CreateBrush(backgroundColor.Value),
                IsHitTestVisible = false
            };
            Canvas.SetLeft(background, point.X);
            Canvas.SetTop(background, point.Y);
            TerminalStyleLayer.Children.Add(background);
        }

        var spanText = text[start..end];
        if (spanText.AsSpan().Trim().Length == 0)
        {
            return;
        }

        var foreground = new TextBlock
        {
            Text = spanText,
            FontFamily = TerminalText.FontFamily,
            FontSize = TerminalText.FontSize,
            FontStyle = TerminalText.FontStyle,
            FontWeight = TerminalText.FontWeight,
            FontStretch = TerminalText.FontStretch,
            Foreground = CreateBrush(ResolveForegroundColor(span)),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(foreground, point.X);
        Canvas.SetTop(foreground, point.Y);
        TerminalStyleLayer.Children.Add(foreground);
    }

    private ScrollViewer? GetTerminalScrollViewer()
    {
        _terminalScrollViewer ??= TerminalText.FindDescendantOfType<ScrollViewer>(true);
        if (_terminalScrollViewer is not null && !_isTerminalScrollViewerSubscribed)
        {
            _terminalScrollViewer.ScrollChanged += OnTerminalScrollChanged;
            _isTerminalScrollViewerSubscribed = true;
        }

        return _terminalScrollViewer;
    }

    private void ResetTerminalScrollViewer()
    {
        if (_terminalScrollViewer is not null && _isTerminalScrollViewerSubscribed)
        {
            _terminalScrollViewer.ScrollChanged -= OnTerminalScrollChanged;
        }

        _terminalScrollViewer = null;
        _isTerminalScrollViewerSubscribed = false;
    }

    private void OnTerminalScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        UpdateTerminalPresentation(_lastSnapshot);
    }

    private static double GetFallbackSpanWidth(
        TextLayout textLayout,
        int start,
        int end,
        Rect startBounds)
    {
        var width = 0.0;
        var previousBounds = startBounds;
        for (var index = start + 1; index <= end; index++)
        {
            var nextBounds = textLayout.HitTestTextPosition(index);
            var delta = nextBounds.X - previousBounds.X;
            width += delta > 1 ? delta : TerminalCellPixelWidth;
            previousBounds = nextBounds;
        }

        if (width <= 0 && end > start)
        {
            width = (end - start) * TerminalCellPixelWidth;
        }

        return width;
    }

    private static int ResolveForegroundColor(TerminalStyleSpan span)
    {
        return span.Inverse
            ? span.BackgroundColor ?? DefaultTerminalBackgroundColor
            : span.ForegroundColor ?? DefaultTerminalForegroundColor;
    }

    private static int? ResolveBackgroundColor(TerminalStyleSpan span)
    {
        return span.Inverse
            ? span.ForegroundColor ?? DefaultTerminalForegroundColor
            : span.BackgroundColor;
    }

    private static IBrush CreateBrush(int color)
    {
        return new SolidColorBrush(Color.FromRgb(
            (byte)(color >> 16 & 0xff),
            (byte)(color >> 8 & 0xff),
            (byte)(color & 0xff)));
    }

    private void UpdateTerminalCursor(TerminalTextSnapshot snapshot)
    {
        if (IsPreview ||
            _session?.Status != TerminalStatus.Connected ||
            TerminalText.Bounds.Width <= 0 ||
            TerminalText.Bounds.Height <= 0)
        {
            TerminalCursor.IsVisible = false;
            return;
        }

        var textPresenter = GetTerminalTextPresenter();
        var textLayout = textPresenter?.TextLayout;
        if (textPresenter is null || textLayout is null)
        {
            TerminalCursor.IsVisible = false;
            return;
        }

        var cursorIndex = Math.Clamp(snapshot.CursorIndex, 0, snapshot.Text.Length);
        var cursorBounds = textLayout.HitTestTextPosition(cursorIndex);
        var cursorPoint = textPresenter.TranslatePoint(
            new Point(cursorBounds.X, cursorBounds.Y),
            TerminalSurface);
        if (cursorPoint is not { } point)
        {
            TerminalCursor.IsVisible = false;
            return;
        }

        var cursorWidth = GetCursorCellWidth(textLayout, snapshot.Text, cursorIndex, cursorBounds);
        var cursorHeight = cursorBounds.Height > 0
            ? cursorBounds.Height
            : TerminalCellPixelHeight;
        if (!IsCursorVisible(point, cursorWidth, cursorHeight))
        {
            TerminalCursor.IsVisible = false;
            return;
        }

        TerminalCursor.Width = cursorWidth;
        TerminalCursor.Height = cursorHeight;
        TerminalCursor.Margin = new Thickness(point.X, point.Y, 0, 0);
        TerminalCursor.IsVisible = true;
    }

    private TextPresenter? GetTerminalTextPresenter()
    {
        _terminalTextPresenter ??= TerminalText.FindDescendantOfType<TextPresenter>(true);
        return _terminalTextPresenter;
    }

    private bool IsCursorVisible(Point point, double cursorWidth, double cursorHeight)
    {
        return point.X + cursorWidth >= 0 &&
               point.X <= TerminalSurface.Bounds.Width &&
               point.Y + cursorHeight >= 0 &&
               point.Y <= TerminalSurface.Bounds.Height;
    }

    private static double GetCursorCellWidth(
        TextLayout textLayout,
        string text,
        int cursorIndex,
        Rect cursorBounds)
    {
        if (cursorIndex < text.Length && text[cursorIndex] != '\n')
        {
            var nextBounds = textLayout.HitTestTextPosition(cursorIndex + 1);
            var width = nextBounds.X - cursorBounds.X;
            if (width > 1)
            {
                return width;
            }
        }

        if (cursorIndex > 0 && text[cursorIndex - 1] != '\n')
        {
            var previousBounds = textLayout.HitTestTextPosition(cursorIndex - 1);
            var width = cursorBounds.X - previousBounds.X;
            if (width > 1)
            {
                return width;
            }
        }

        return TerminalCellPixelWidth;
    }

    private static string? TranslateKeyInput(KeyEventArgs e)
    {
        if (HasControl(e.KeyModifiers) && !HasAlt(e.KeyModifiers))
        {
            var controlInput = TranslateControlInput(e.Key);
            if (controlInput is not null)
            {
                return controlInput;
            }
        }

        return e.Key switch
        {
            AvaloniaKey.Enter or AvaloniaKey.Return => "\r",
            AvaloniaKey.Back => "\u007f",
            AvaloniaKey.Tab when HasShift(e.KeyModifiers) => "\u001b[Z",
            AvaloniaKey.Tab => "\t",
            AvaloniaKey.Escape => "\u001b",
            AvaloniaKey.Up => "\u001b[A",
            AvaloniaKey.Down => "\u001b[B",
            AvaloniaKey.Right => "\u001b[C",
            AvaloniaKey.Left => "\u001b[D",
            AvaloniaKey.Home => "\u001b[H",
            AvaloniaKey.End => "\u001b[F",
            AvaloniaKey.Insert => "\u001b[2~",
            AvaloniaKey.Delete => "\u001b[3~",
            AvaloniaKey.PageUp => "\u001b[5~",
            AvaloniaKey.PageDown => "\u001b[6~",
            _ => null
        };
    }

    private static string? TranslateControlInput(AvaloniaKey key)
    {
        var keyName = key.ToString();
        if (keyName.Length == 1 &&
            keyName[0] is >= 'A' and <= 'Z')
        {
            return ((char)(keyName[0] - 'A' + 1)).ToString();
        }

        return key switch
        {
            AvaloniaKey.Space => "\0",
            AvaloniaKey.OemOpenBrackets => "\u001b",
            AvaloniaKey.OemPipe or AvaloniaKey.OemBackslash => "\u001c",
            AvaloniaKey.OemCloseBrackets => "\u001d",
            _ => null
        };
    }

    private static bool IsTerminalCopyGesture(KeyEventArgs e)
    {
        return e.Key == AvaloniaKey.C && HasOnlyControlShift(e.KeyModifiers);
    }

    private static bool IsTerminalPasteGesture(KeyEventArgs e)
    {
        return e.Key == AvaloniaKey.V && HasOnlyControlShift(e.KeyModifiers);
    }

    private static bool HasOnlyControlShift(AvaloniaKeyModifiers keyModifiers)
    {
        const AvaloniaKeyModifiers required = AvaloniaKeyModifiers.Control | AvaloniaKeyModifiers.Shift;
        return (keyModifiers & required) == required &&
               (keyModifiers & ~required) == AvaloniaKeyModifiers.None;
    }

    private static bool HasControl(AvaloniaKeyModifiers keyModifiers)
    {
        return (keyModifiers & AvaloniaKeyModifiers.Control) == AvaloniaKeyModifiers.Control;
    }

    private static bool HasShift(AvaloniaKeyModifiers keyModifiers)
    {
        return (keyModifiers & AvaloniaKeyModifiers.Shift) == AvaloniaKeyModifiers.Shift;
    }

    private static bool HasAlt(AvaloniaKeyModifiers keyModifiers)
    {
        return (keyModifiers & AvaloniaKeyModifiers.Alt) == AvaloniaKeyModifiers.Alt;
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        DisposeTerminalPane();
    }

    public void DisposeTerminalPane()
    {
        if (_isDisposing)
        {
            return;
        }

        _isDisposing = true;
        DataContextChanged -= OnDataContextChanged;
        DetachedFromVisualTree -= OnDetachedFromVisualTree;
        Loaded -= OnLoaded;
        TerminalText.PropertyChanged -= OnTerminalTextPropertyChanged;
        TerminalText.RemoveHandler(KeyDownEvent, OnTerminalKeyDown);
        TerminalText.RemoveHandler(TextInputEvent, OnTerminalTextInput);
        TerminalText.RemoveHandler(PointerPressedEvent, OnTerminalPointerPressed);
        ResetTerminalScrollViewer();

        if (_session is not null)
        {
            DetachSession(_session);
        }

        _session = null;
    }
}