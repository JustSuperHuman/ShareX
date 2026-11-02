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
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using ShareX.AvaloniaUI.Theming;
using ShareX.Tools.Localization;
using System.Runtime.InteropServices;

namespace ShareX.Tools;

// The floating camera picture. It sits on top of the desktop, so whatever records the screen picks it
// up exactly as the user sees it; no compositing step is needed. Drag it anywhere; hover for controls.
public sealed class CameraBubbleWindow : Window
{
    private const int SmallSize = 230;
    private const int LargeSize = 400;
    private const int MinimumSize = 150;
    private const int MaximumSize = 600;
    private const double MaximumZoom = 3;
    private const double ZoomStep = 0.1;
    private const double ToolbarHeight = 84;
    private const double Shadow = 24;
    private const double ResizeHandleSize = 28;

    private static CameraBubbleWindow? _current;

    private readonly RecordingStudioOptions _options;
    private readonly string _ffmpegPath;
    private readonly string _device;
    private readonly CameraFormat? _format;
    private readonly CameraFeed _feed;
    private readonly WriteableBitmap _bitmap;
    private readonly byte[] _pendingFrame;
    private readonly Image _image;
    private readonly Border _bubble;
    private readonly Border _shadow;
    private readonly Panel _surface;
    private readonly Border _resizeHandle;
    private readonly Border _toolbar;
    private readonly Button _zoomOut;
    private readonly Button _zoomIn;
    private readonly Button _zoomReset;
    private readonly TextBlock _zoomLabel;
    private readonly TextBlock _status;
    private int _framePending;
    private bool _positioned;
    private bool _resizing;
    private volatile bool _closed;
    private PixelPoint _resizeStart;
    private int _resizeStartSize;
    private double _resizeScale;

    public static bool IsOpen => _current != null;

    // Raised when the user closes the bubble with its own close button.
    public static event Action? ClosedByUser;

    public static void ShowCamera(string ffmpegPath, string device, CameraFormat? format, RecordingStudioOptions options)
    {
        if (_current is { } current && ReferenceEquals(current._options, options) &&
            current._ffmpegPath == ffmpegPath && current._device == device && current._format == format)
        {
            if (!current.IsVisible) current.Show();
            return;
        }

        CloseCamera();
        _current = new CameraBubbleWindow(ffmpegPath, device, format, options);
        _current.Show();
    }

    public static void CloseCamera()
    {
        CameraBubbleWindow? window = _current;
        _current = null;
        window?.Close();
    }

    private CameraBubbleWindow(string ffmpegPath, string device, CameraFormat? format, RecordingStudioOptions options)
    {
        _options = options;
        _ffmpegPath = ffmpegPath;
        _device = device;
        _format = format;

        Title = "ShareX Camera";
        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        RequestedThemeVariant = ThemeManager.GetCurrentTheme();

        _feed = new CameraFeed(ffmpegPath, device, format);
        _bitmap = new WriteableBitmap(new PixelSize(_feed.FrameWidth, CameraFeed.FrameHeight), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        _pendingFrame = new byte[_feed.FrameWidth * CameraFeed.FrameHeight * 4];

        _image = new Image
        {
            Source = _bitmap,
            Stretch = Stretch.UniformToFill,
            RenderTransformOrigin = RelativePoint.Center
        };
        RenderOptions.SetBitmapInterpolationMode(_image, BitmapInterpolationMode.HighQuality);

        _status = new TextBlock
        {
            Text = "Starting camera...",
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(18)
        };

        _bubble = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(24, 24, 27)),
            ClipToBounds = true,
            Cursor = new Cursor(StandardCursorType.SizeAll),
            Child = new Panel { Children = { _image, _status } }
        };
        _bubble.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        };
        _bubble.PointerWheelChanged += (_, e) =>
        {
            SetZoom(_options.CameraZoom + e.Delta.Y * ZoomStep);
            e.Handled = true;
        };

        // Only the picture clips its children. Clipping the shadow's border cuts the soft edge off.
        _shadow = new Border
        {
            Background = _bubble.Background,
            BoxShadow = BoxShadows.Parse("0 4 14 0 #70000000"),
            Child = _bubble
        };

        TextBlock resizeGlyph = new()
        {
            Text = LucideIcons.move_diagonal_2,
            FontSize = 16,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        resizeGlyph.Classes.Add("icon");
        _resizeHandle = new Border
        {
            Width = ResizeHandleSize,
            Height = ResizeHandleSize,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(210, 24, 24, 27)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Cursor = new Cursor(StandardCursorType.BottomRightCorner),
            Opacity = 0,
            IsHitTestVisible = false,
            Child = resizeGlyph
        };
        ToolTip.SetTip(_resizeHandle, Strings.CameraBubble_Resize);
        AutomationProperties.SetName(_resizeHandle, Strings.CameraBubble_Resize);
        _resizeHandle.PointerPressed += OnResizePressed;
        _resizeHandle.PointerMoved += OnResizeMoved;
        _resizeHandle.PointerReleased += (_, e) =>
        {
            if (!_resizing) return;
            _resizing = false;
            e.Pointer.Capture(null);
            KeepOnScreen();
            UpdateControls();
            e.Handled = true;
        };
        _resizeHandle.PointerCaptureLost += (_, _) =>
        {
            if (!_resizing) return;
            _resizing = false;
            KeepOnScreen();
            UpdateControls();
        };

        _surface = new Panel
        {
            Margin = new Thickness(Shadow),
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { _shadow, _resizeHandle }
        };

        _zoomOut = ToolbarButton(LucideIcons.minus, Strings.CameraBubble_ZoomOut, () => SetZoom(_options.CameraZoom - ZoomStep));
        _zoomIn = ToolbarButton(LucideIcons.plus, Strings.CameraBubble_ZoomIn, () => SetZoom(_options.CameraZoom + ZoomStep));
        _zoomReset = ToolbarButton(string.Empty, Strings.CameraBubble_ResetZoom, () => SetZoom(1));
        _zoomReset.Width = 70;
        _zoomLabel = new TextBlock { FontSize = 13, Foreground = Brushes.White };
        _zoomReset.Content = _zoomLabel;

        _toolbar = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(24, 24, 27)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(4),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0,
            IsHitTestVisible = false,
            Child = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 2,
                        Children =
                        {
                            ToolbarButton(LucideIcons.x, "Turn the camera off", () =>
                            {
                                CloseCamera();
                                ClosedByUser?.Invoke();
                            }),
                            ToolbarButton(LucideIcons.maximize_2, "Switch between small and large", () =>
                            {
                                _options.CameraSize = _options.CameraSize >= LargeSize ? SmallSize : LargeSize;
                                ApplyLayout();
                            }),
                            ToolbarButton(LucideIcons.circle, "Change shape: round, square or wide", () =>
                            {
                                _options.CameraShape = (CameraBubbleShape)(((int)_options.CameraShape + 1) % 3);
                                ApplyLayout();
                            }),
                            ToolbarButton(LucideIcons.flip_horizontal, "Mirror the picture", () =>
                            {
                                _options.CameraMirrored = !_options.CameraMirrored;
                                ApplyZoom();
                            })
                        }
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 2,
                        Children = { _zoomOut, _zoomReset, _zoomIn }
                    }
                }
            }
        };

        Grid root = new() { RowDefinitions = new RowDefinitions($"{ToolbarHeight},*"), Background = Brushes.Transparent };
        Grid.SetRow(_toolbar, 0);
        Grid.SetRow(_surface, 1);
        root.Children.Add(_toolbar);
        root.Children.Add(_surface);
        Content = root;

        PointerEntered += (_, _) => UpdateControls();
        PointerExited += (_, _) => UpdateControls();
        PositionChanged += (_, e) =>
        {
            if (!_positioned) return;
            _options.CameraX = e.Point.X;
            _options.CameraY = e.Point.Y;
        };

        _feed.FrameReady += OnFrameReady;
        _feed.Failed += () => Dispatcher.UIThread.Post(() =>
        {
            if (_closed) return;
            _status.Text = "This camera isn't sending a picture. It may be in use by another app.";
            _status.IsVisible = true;
        });

        ApplyLayout();
        Opened += (_, _) => PlaceOnScreen();
        Closed += (_, _) =>
        {
            _closed = true;
            _feed.FrameReady -= OnFrameReady;
            _feed.Dispose();
            _bitmap.Dispose();
            if (ReferenceEquals(_current, this)) _current = null;
        };
    }

    private static Button ToolbarButton(string icon, string tip, Action action)
    {
        TextBlock glyph = new() { Text = icon, FontSize = 16 };
        glyph.Classes.Add("icon");

        Button button = new()
        {
            Content = glyph,
            Width = 34,
            Height = 34,
            MinWidth = 0,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Brushes.White,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(button, tip);
        AutomationProperties.SetName(button, tip);
        button.Click += (_, _) => action();
        return button;
    }

    private void ApplyLayout()
    {
        double aspect = _options.CameraShape == CameraBubbleShape.Wide ? 16d / 9 : 1;
        Screen? screen = _positioned ? Screens.ScreenFromWindow(this) : SavedScreen();
        screen ??= Screens.Primary;
        double maxSize = screen == null ? MaximumSize : Math.Min(MaximumSize, Math.Min(
            (screen.WorkingArea.Width / screen.Scaling - Shadow * 2) / aspect,
            screen.WorkingArea.Height / screen.Scaling - Shadow * 2 - ToolbarHeight));
        _options.CameraSize = Math.Clamp(_options.CameraSize, MinimumSize, Math.Max(MinimumSize, (int)maxSize));
        double size = _options.CameraSize;
        double width = Math.Round(size * aspect);

        _surface.Width = width;
        _surface.Height = size;
        _bubble.CornerRadius = new CornerRadius(_options.CameraShape == CameraBubbleShape.Round ? size / 2 : Math.Max(18, size * 0.09));
        _shadow.CornerRadius = _bubble.CornerRadius;
        double handleInset = _options.CameraShape == CameraBubbleShape.Round
            ? Math.Max(0, size * (1 - Math.Sqrt(0.5)) / 2 - ResizeHandleSize / 2)
            : 6;
        _resizeHandle.Margin = new Thickness(0, 0, handleInset, handleInset);
        ApplyZoom();

        Width = Math.Max(width, 160) + Shadow * 2;
        Height = size + Shadow * 2 + ToolbarHeight;
        if (_positioned && !_resizing) KeepOnScreen();
    }

    private void SetZoom(double zoom)
    {
        _options.CameraZoom = zoom;
        ApplyZoom();
    }

    private void ApplyZoom()
    {
        double zoom = double.IsFinite(_options.CameraZoom) ? Math.Clamp(_options.CameraZoom, 1, MaximumZoom) : 1;
        _options.CameraZoom = zoom;
        _image.RenderTransform = new ScaleTransform(_options.CameraMirrored ? -zoom : zoom, zoom);
        _zoomLabel.Text = $"{zoom:0.0}×";
        _zoomOut.IsEnabled = zoom > 1;
        _zoomIn.IsEnabled = zoom < MaximumZoom;
    }

    private void UpdateControls()
    {
        bool visible = IsPointerOver || _resizing;
        _toolbar.Opacity = _resizeHandle.Opacity = visible ? 1 : 0;
        _toolbar.IsHitTestVisible = _resizeHandle.IsHitTestVisible = visible;
    }

    private void OnResizePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _resizeStart = this.PointToScreen(e.GetPosition(this));
        _resizeStartSize = _options.CameraSize;
        _resizeScale = RenderScaling;
        _resizing = true;
        e.Pointer.Capture(_resizeHandle);
        UpdateControls();
        e.Handled = true;
    }

    private void OnResizeMoved(object? sender, PointerEventArgs e)
    {
        if (!_resizing) return;
        PixelPoint pointer = this.PointToScreen(e.GetPosition(this));
        double dx = (pointer.X - _resizeStart.X) / _resizeScale;
        double dy = (pointer.Y - _resizeStart.Y) / _resizeScale;
        double aspect = _options.CameraShape == CameraBubbleShape.Wide ? 16d / 9 : 1;
        double anchor = _options.CameraShape == CameraBubbleShape.Round ? (1 + Math.Sqrt(0.5)) / 2 : 1;
        double delta = (dx * aspect + dy) / ((aspect * aspect + 1) * anchor);
        _options.CameraSize = (int)Math.Clamp(Math.Round(_resizeStartSize + delta), MinimumSize, MaximumSize);
        ApplyLayout();
        e.Handled = true;
    }

    private void KeepOnScreen()
    {
        Screen? screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen == null) return;
        PixelRect area = screen.WorkingArea;
        Position = new PixelPoint(
            Math.Clamp(Position.X, area.X, Math.Max(area.X, area.Right - (int)Math.Ceiling(Width * screen.Scaling))),
            Math.Clamp(Position.Y, area.Y, Math.Max(area.Y, area.Bottom - (int)Math.Ceiling(Height * screen.Scaling))));
        _options.CameraX = Position.X;
        _options.CameraY = Position.Y;
    }

    private Screen? SavedScreen() => _options.CameraX != int.MinValue && _options.CameraY != int.MinValue
        ? Screens.ScreenFromPoint(new PixelPoint(_options.CameraX + 40, _options.CameraY + 40))
        : null;

    private void PlaceOnScreen()
    {
        Screen? screen = Screens.Primary;
        PixelPoint position;

        if (SavedScreen() != null)
        {
            position = new PixelPoint(_options.CameraX, _options.CameraY);
        }
        else if (screen != null)
        {
            double scale = screen.Scaling;
            position = new PixelPoint(
                screen.WorkingArea.Right - (int)((Width + 40) * scale),
                screen.WorkingArea.Bottom - (int)((Height + 40) * scale));
        }
        else
        {
            position = new PixelPoint(100, 100);
        }

        Position = position;
        _positioned = true;
        ApplyLayout();
    }

    private void OnFrameReady(byte[] frame)
    {
        // Skip frames the UI hasn't caught up with instead of queueing them behind a busy dispatcher.
        if (_closed || Interlocked.CompareExchange(ref _framePending, 1, 0) != 0) return;

        Buffer.BlockCopy(frame, 0, _pendingFrame, 0, _pendingFrame.Length);
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (_closed) return;
                using (ILockedFramebuffer buffer = _bitmap.Lock())
                {
                    int rowBytes = _feed.FrameWidth * 4;
                    for (int y = 0; y < CameraFeed.FrameHeight; y++)
                    {
                        Marshal.Copy(_pendingFrame, y * rowBytes, buffer.Address + y * buffer.RowBytes, rowBytes);
                    }
                }

                if (_status.IsVisible) _status.IsVisible = false;
                _image.InvalidateVisual();
            }
            finally
            {
                Interlocked.Exchange(ref _framePending, 0);
            }
        }, DispatcherPriority.Render);
    }
}
