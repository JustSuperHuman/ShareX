#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.
*/

#endregion License Information (GPL v3)

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using ShareX.AvaloniaUI.Theming;

namespace ShareX.Tools;

public partial class RecordingStudioWindow : Window
{
    private static RecordingStudioWindow? _current;

    private readonly RecordingStudioViewModel _viewModel;
    private readonly RecordingStudioOptions _options;
    private bool _opened;
    private bool _positioned;
    private bool _picking;
    private bool _closed;

    public RecordingStudioWindow()
        : this(new RecordingStudioOptions(), new RecordingStudioServices(string.Empty, () => [], _ => { }, false, () => { }, () => { }))
    {
    }

    public RecordingStudioWindow(RecordingStudioOptions options, RecordingStudioServices services)
    {
        _options = options;
        _viewModel = new RecordingStudioViewModel(options, services);
        DataContext = _viewModel;
        InitializeComponent();
        RequestedThemeVariant = ThemeManager.GetCurrentTheme();
        _viewModel.CloseRequested = Close;
        _viewModel.PickRequested = PickOnScreen;
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
        Opened += async (_, _) =>
        {
            // A picker temporarily hides this window. Showing it again must not reload devices,
            // restart the camera or recenter the setup window.
            if (_opened) return;
            _opened = true;
            PlaceOnScreen();
            await _viewModel.InitializeAsync();
            if (!_closed) KeepOnScreen();
        };
        PositionChanged += (_, e) =>
        {
            if (!_positioned || WindowState != WindowState.Normal) return;
            _options.SetupX = e.Point.X;
            _options.SetupY = e.Point.Y;
        };
        SizeChanged += (_, _) =>
        {
            if (_positioned && WindowState == WindowState.Normal) KeepOnScreen();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _viewModel.Dispose();
            if (ReferenceEquals(_current, this)) _current = null;
        };
    }

    // Only one setup window at a time: asking for it again brings the open one forward.
    public static void ShowSingle(RecordingStudioOptions options, RecordingStudioServices services)
    {
        if (_current != null)
        {
            if (_current.WindowState == WindowState.Minimized) _current.WindowState = WindowState.Normal;
            if (!_current.IsVisible && !_current._picking) _current.Show();
            _current.Activate();
            return;
        }

        _current = new RecordingStudioWindow(options, services);
        _current.Show();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void PlaceOnScreen()
    {
        PixelPoint saved = new(_options.SetupX, _options.SetupY);
        Screen? screen = Screens.Primary;
        if (_options.SetupX != int.MinValue && _options.SetupY != int.MinValue &&
            Screens.ScreenFromPoint(saved + new PixelPoint(40, 40)) != null)
        {
            Position = saved;
        }
        else if (screen != null)
        {
            Size size = FrameSize ?? ClientSize;
            Position = new PixelPoint(
                screen.WorkingArea.X + (int)Math.Round((screen.WorkingArea.Width - size.Width * screen.Scaling) / 2),
                screen.WorkingArea.Y + (int)Math.Round((screen.WorkingArea.Height - size.Height * screen.Scaling) / 2));
        }

        _positioned = true;
        KeepOnScreen();
    }

    private void KeepOnScreen()
    {
        if (WindowState != WindowState.Normal) return;
        Screen? screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen == null) return;
        Size size = FrameSize ?? ClientSize;
        PixelRect area = screen.WorkingArea;
        Position = new PixelPoint(
            Math.Clamp(Position.X, area.X, Math.Max(area.X, area.Right - (int)Math.Ceiling(size.Width * screen.Scaling))),
            Math.Clamp(Position.Y, area.Y, Math.Max(area.Y, area.Bottom - (int)Math.Ceiling(size.Height * screen.Scaling))));
        _options.SetupX = Position.X;
        _options.SetupY = Position.Y;
    }

    // Steps out of the way, lets the user point at the display or window to record, then comes back
    // with it selected.
    private void PickOnScreen(RecordingTargetKind kind)
    {
        if (_picking || _closed) return;
        IReadOnlyList<TargetPickerItem> items = _viewModel.GetPickerItems(kind);
        if (items.Count == 0) return;

        bool display = kind == RecordingTargetKind.Display;
        PixelPoint position = Position;
        _picking = true;
        Hide();
        TargetPickerOverlay.Pick(
            items,
            display ? "Click the display you want to record  ·  Esc to cancel" : "Click the window you want to record  ·  Esc to cancel",
            display ? LucideIcons.monitor : LucideIcons.app_window,
            result =>
            {
                _picking = false;
                if (_closed) return;
                Show();
                Position = position;
                KeepOnScreen();
                Activate();
                _viewModel.ApplyPick(result);
            });
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && FocusManager?.GetFocusedElement() is not Button and not ComboBox and not ComboBoxItem)
        {
            _viewModel.StartCommand.Execute(null);
            e.Handled = true;
        }
    }
}
