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
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using ShareX.AvaloniaUI.Theming;
using ShareX.HelpersLib;
using System.Globalization;
using Bitmap = Avalonia.Media.Imaging.Bitmap;
using DrawingRectangle = System.Drawing.Rectangle;

namespace ShareX.Tools;

// What the cursor is over while picking: a rectangle in screen pixels plus the text shown inside it.
public sealed record TargetPickerItem(DrawingRectangle Bounds, string Title, string Detail, Bitmap? Icon, object Value);

// Dims every display and lights up the window (or display) under the cursor, so a recording target
// can be chosen by pointing at it. One borderless window per display; click picks, Esc cancels.
public sealed class TargetPickerOverlay
{
    private readonly List<OverlayWindow> _windows = [];
    private readonly IReadOnlyList<TargetPickerItem> _items;
    private readonly Action<TargetPickerItem?> _completed;
    private readonly DispatcherTimer _timer;
    private TargetPickerItem? _hovered;
    private bool _finished;

    // Items must be ordered front to back: the first one under the cursor wins.
    public static void Pick(IReadOnlyList<TargetPickerItem> items, string hint, string glyph, Action<TargetPickerItem?> completed)
    {
        _ = new TargetPickerOverlay(items, hint, glyph, completed);
    }

    private TargetPickerOverlay(IReadOnlyList<TargetPickerItem> items, string hint, string glyph, Action<TargetPickerItem?> completed)
    {
        _items = items;
        _completed = completed;

        Window anchor = new();
        foreach (Screen screen in anchor.Screens.All)
        {
            OverlayWindow window = new(screen, hint, glyph);
            window.PointerPressed += OnPointerPressed;
            window.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) Finish(null);
            };
            _windows.Add(window);
        }

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _timer.Tick += (_, _) => Track();

        foreach (OverlayWindow window in _windows) window.Show();
        _windows.FirstOrDefault(w => w.Screen.IsPrimary)?.Activate();
        Track();
        _timer.Start();
    }

    private void Track()
    {
        System.Drawing.Point cursor = CaptureHelpers.GetCursorPosition();
        TargetPickerItem? hovered = _items.FirstOrDefault(item => item.Bounds.Contains(cursor));
        if (ReferenceEquals(hovered, _hovered)) return;

        _hovered = hovered;
        foreach (OverlayWindow window in _windows) window.SetHovered(hovered);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        PointerPointProperties properties = e.GetCurrentPoint(null).Properties;

        if (properties.IsRightButtonPressed)
        {
            Finish(null);
        }
        else if (properties.IsLeftButtonPressed)
        {
            Track();
            if (_hovered != null) Finish(_hovered);
        }
    }

    private void Finish(TargetPickerItem? result)
    {
        if (_finished) return;
        _finished = true;
        _timer.Stop();
        foreach (OverlayWindow window in _windows) window.Close();
        _completed(result);
    }

    private sealed class OverlayWindow : Window
    {
        private readonly OverlaySurface _surface;

        public Screen Screen { get; }

        public OverlayWindow(Screen screen, string hint, string glyph)
        {
            Screen = screen;
            Title = "ShareX Target Picker";
            WindowDecorations = WindowDecorations.None;
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = screen.Bounds.Position;
            Width = screen.Bounds.Width / screen.Scaling;
            Height = screen.Bounds.Height / screen.Scaling;
            Cursor = new Cursor(StandardCursorType.Hand);
            RequestedThemeVariant = ThemeManager.GetCurrentTheme();

            _surface = new OverlaySurface(screen, hint, glyph);
            Content = _surface;
        }

        public void SetHovered(TargetPickerItem? item) => _surface.SetHovered(item);
    }

    private sealed class OverlaySurface : Control
    {
        private static readonly IBrush Dim = new SolidColorBrush(Color.FromArgb(165, 0, 0, 0));
        private static readonly IBrush Tint = new SolidColorBrush(Color.FromArgb(105, 37, 99, 235));
        private static readonly IPen Outline = new Pen(new SolidColorBrush(Color.FromRgb(96, 165, 250)), 2);
        private static readonly IBrush HintBackground = new SolidColorBrush(Color.FromArgb(225, 24, 24, 27));
        private static readonly Typeface TitleFace = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
        private static readonly Typeface TextFace = new(FontFamily.Default);
        private static readonly Typeface IconFace = new(new FontFamily("avares://ShareX.Avalonia/Assets#lucide"));

        private readonly Screen _screen;
        private readonly string _hint;
        private readonly string _glyph;
        private TargetPickerItem? _hovered;

        public OverlaySurface(Screen screen, string hint, string glyph)
        {
            _screen = screen;
            _hint = hint;
            _glyph = glyph;
        }

        public void SetHovered(TargetPickerItem? item)
        {
            _hovered = item;
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            Rect bounds = new(Bounds.Size);
            context.FillRectangle(Dim, bounds);

            if (_hovered != null)
            {
                // Screen pixels -> this display's own device-independent coordinates.
                double scale = _screen.Scaling;
                Rect target = new(
                    (_hovered.Bounds.X - _screen.Bounds.X) / scale,
                    (_hovered.Bounds.Y - _screen.Bounds.Y) / scale,
                    _hovered.Bounds.Width / scale,
                    _hovered.Bounds.Height / scale);
                Rect visible = target.Intersect(bounds);

                if (visible.Width > 0 && visible.Height > 0)
                {
                    context.FillRectangle(Tint, visible);
                    context.DrawRectangle(null, Outline, visible.Deflate(1));
                    DrawLabel(context, visible, _hovered);
                }
            }

            DrawHint(context, bounds);
        }

        private void DrawLabel(DrawingContext context, Rect area, TargetPickerItem item)
        {
            FormattedText title = Text(Shorten(item.Title, 48), TitleFace, 26);
            FormattedText detail = Text(item.Detail, TextFace, 13);
            double iconSize = 72;
            double height = iconSize + 14 + title.Height + 6 + detail.Height;
            double y = area.Center.Y - height / 2;

            using (context.PushClip(area))
            {
                Rect iconRect = new(area.Center.X - iconSize / 2, y, iconSize, iconSize);
                if (item.Icon != null)
                {
                    context.DrawImage(item.Icon, new Rect(0, 0, item.Icon.PixelSize.Width, item.Icon.PixelSize.Height), iconRect);
                }
                else
                {
                    FormattedText glyph = Text(_glyph, IconFace, 64);
                    context.DrawText(glyph, new Point(area.Center.X - glyph.Width / 2, y + (iconSize - glyph.Height) / 2));
                }

                y += iconSize + 14;
                context.DrawText(title, new Point(area.Center.X - title.Width / 2, y));
                y += title.Height + 6;
                context.DrawText(detail, new Point(area.Center.X - detail.Width / 2, y));
            }
        }

        private void DrawHint(DrawingContext context, Rect bounds)
        {
            FormattedText hint = Text(_hint, TextFace, 14);
            Rect pill = new(bounds.Center.X - hint.Width / 2 - 18, 28, hint.Width + 36, hint.Height + 18);
            context.DrawRectangle(HintBackground, null, pill, 12, 12);
            context.DrawText(hint, new Point(pill.X + 18, pill.Y + 9));
        }

        private static FormattedText Text(string text, Typeface typeface, double size) =>
            new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size, Brushes.White);

        private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
    }
}
