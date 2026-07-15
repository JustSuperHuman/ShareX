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
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;

namespace ShareX.Tools.Controls;

public readonly record struct TimelineZoomRegion(double Start, double End);

public sealed class VideoTimelineControl : Control
{
    public static readonly StyledProperty<double> DurationProperty =
        AvaloniaProperty.Register<VideoTimelineControl, double>(nameof(Duration), 1d);

    public static readonly StyledProperty<IReadOnlyList<TimelineZoomRegion>?> ZoomRegionsProperty =
        AvaloniaProperty.Register<VideoTimelineControl, IReadOnlyList<TimelineZoomRegion>?>(nameof(ZoomRegions));

    public static readonly StyledProperty<double> InPointProperty =
        AvaloniaProperty.Register<VideoTimelineControl, double>(nameof(InPoint), 0d, defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<double> OutPointProperty =
        AvaloniaProperty.Register<VideoTimelineControl, double>(nameof(OutPoint), 1d, defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<double> PositionProperty =
        AvaloniaProperty.Register<VideoTimelineControl, double>(nameof(Position), 0d, defaultBindingMode: BindingMode.TwoWay);

    private const double HorizontalPadding = 12;
    private const double HandleWidth = 10;
    private const double MinimumSelectionSeconds = 0.05;
    private DragTarget _dragTarget;

    static VideoTimelineControl()
    {
        AffectsRender<VideoTimelineControl>(DurationProperty, InPointProperty, OutPointProperty, PositionProperty, ZoomRegionsProperty);
    }

    public VideoTimelineControl()
    {
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);
        MinHeight = 52;
    }

    public double Duration { get => GetValue(DurationProperty); set => SetValue(DurationProperty, value); }
    public IReadOnlyList<TimelineZoomRegion>? ZoomRegions { get => GetValue(ZoomRegionsProperty); set => SetValue(ZoomRegionsProperty, value); }
    public double InPoint { get => GetValue(InPointProperty); set => SetValue(InPointProperty, value); }
    public double OutPoint { get => GetValue(OutPointProperty); set => SetValue(OutPointProperty, value); }
    public double Position { get => GetValue(PositionProperty); set => SetValue(PositionProperty, value); }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        Rect track = GetTrackBounds();
        IBrush background = GetBrush("ShareX.Brush.Control.Background", new SolidColorBrush(Color.Parse("#343434")));
        IBrush selection = GetBrush("ShareX.Brush.Accent.Start", new SolidColorBrush(Color.Parse("#3E83F2")));
        IBrush foreground = GetBrush("ShareX.Brush.Text", Brushes.White);
        IBrush muted = GetBrush("ShareX.Brush.Text.Secondary", Brushes.Gray);

        context.DrawRectangle(background, null, track, 5, 5);

        double inX = TimeToX(InPoint, track);
        double outX = TimeToX(OutPoint, track);
        Rect selected = new(inX, track.Top, Math.Max(0, outX - inX), track.Height);
        context.DrawRectangle(selection, null, selected, 4, 4);

        IReadOnlyList<TimelineZoomRegion>? zoomRegions = ZoomRegions;
        if (zoomRegions != null && zoomRegions.Count > 0)
        {
            IBrush zoomBrush = new SolidColorBrush(Color.FromArgb(210, 255, 202, 87));
            double barHeight = 4;
            double barTop = track.Top - barHeight - 3;
            foreach (TimelineZoomRegion region in zoomRegions)
            {
                double startX = TimeToX(region.Start, track);
                double endX = TimeToX(region.End, track);
                Rect bar = new(startX, barTop, Math.Max(2, endX - startX), barHeight);
                context.DrawRectangle(zoomBrush, null, bar, 2, 2);
            }
        }

        DrawHandle(context, inX, track, selection, foreground);
        DrawHandle(context, outX, track, selection, foreground);

        double positionX = TimeToX(Position, track);
        context.DrawLine(new Pen(foreground, 2), new Point(positionX, track.Top - 8), new Point(positionX, track.Bottom + 8));
        StreamGeometry triangle = new();
        using (StreamGeometryContext geometry = triangle.Open())
        {
            geometry.BeginFigure(new Point(positionX - 5, track.Top - 9), true);
            geometry.LineTo(new Point(positionX + 5, track.Top - 9));
            geometry.LineTo(new Point(positionX, track.Top - 3));
            geometry.EndFigure(true);
        }
        context.DrawGeometry(foreground, null, triangle);

        FormattedText start = CreateText(FormatTime(InPoint), muted);
        FormattedText end = CreateText(FormatTime(OutPoint), muted);
        context.DrawText(start, new Point(track.Left, track.Bottom + 9));
        context.DrawText(end, new Point(track.Right - end.Width, track.Bottom + 9));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || Duration <= 0)
        {
            return;
        }

        Focus();
        Rect track = GetTrackBounds();
        Point point = e.GetPosition(this);
        double inDistance = Math.Abs(point.X - TimeToX(InPoint, track));
        double outDistance = Math.Abs(point.X - TimeToX(OutPoint, track));

        _dragTarget = Math.Min(inDistance, outDistance) <= HandleWidth + 3
            ? (inDistance <= outDistance ? DragTarget.InPoint : DragTarget.OutPoint)
            : DragTarget.Position;

        e.Pointer.Capture(this);
        UpdateFromPointer(point, track);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragTarget != DragTarget.None)
        {
            UpdateFromPointer(e.GetPosition(this), GetTrackBounds());
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragTarget != DragTarget.None)
        {
            UpdateFromPointer(e.GetPosition(this), GetTrackBounds());
            _dragTarget = DragTarget.None;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        double step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 1 : 0.1;
        if (e.Key is Key.Left or Key.Right)
        {
            double direction = e.Key == Key.Left ? -1 : 1;
            SetCurrentValue(PositionProperty, Math.Clamp(Position + direction * step, InPoint, OutPoint));
            e.Handled = true;
        }
    }

    private void UpdateFromPointer(Point point, Rect track)
    {
        double time = XToTime(point.X, track);
        switch (_dragTarget)
        {
            case DragTarget.InPoint:
                SetCurrentValue(InPointProperty, Math.Clamp(time, 0, Math.Max(0, OutPoint - MinimumSelectionSeconds)));
                SetCurrentValue(PositionProperty, InPoint);
                break;
            case DragTarget.OutPoint:
                SetCurrentValue(OutPointProperty, Math.Clamp(time, Math.Min(Duration, InPoint + MinimumSelectionSeconds), Duration));
                SetCurrentValue(PositionProperty, OutPoint);
                break;
            case DragTarget.Position:
                SetCurrentValue(PositionProperty, Math.Clamp(time, InPoint, OutPoint));
                break;
        }
    }

    private Rect GetTrackBounds() => new(HorizontalPadding, 14, Math.Max(1, Bounds.Width - HorizontalPadding * 2), 18);
    private double TimeToX(double time, Rect track) => track.Left + Math.Clamp(time / Math.Max(Duration, 0.001), 0, 1) * track.Width;
    private double XToTime(double x, Rect track) => Math.Clamp((x - track.Left) / track.Width, 0, 1) * Duration;

    private static void DrawHandle(DrawingContext context, double x, Rect track, IBrush fill, IBrush stroke)
    {
        Rect handle = new(x - HandleWidth / 2, track.Top - 4, HandleWidth, track.Height + 8);
        context.DrawRectangle(fill, new Pen(stroke, 1), handle, 3, 3);
        context.DrawLine(new Pen(stroke, 1), new Point(x, handle.Top + 7), new Point(x, handle.Bottom - 7));
    }

    private IBrush GetBrush(string key, IBrush fallback) =>
        Application.Current?.TryGetResource(key, ActualThemeVariant, out object? value) == true && value is IBrush brush ? brush : fallback;

    private static FormattedText CreateText(string text, IBrush brush) => new(
        text,
        System.Globalization.CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight,
        new Typeface("Segoe UI"),
        11,
        brush);

    private static string FormatTime(double seconds)
    {
        TimeSpan value = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss\.f") : value.ToString(@"m\:ss\.f");
    }

    private enum DragTarget
    {
        None,
        InPoint,
        OutPoint,
        Position
    }
}
