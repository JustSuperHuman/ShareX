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

public readonly record struct TimelineSpeedSegment(double Start, double End, double Speed, bool Removed = false);

public sealed class VideoTimelineControl : Control
{
    public static readonly StyledProperty<double> DurationProperty =
        AvaloniaProperty.Register<VideoTimelineControl, double>(nameof(Duration), 1d);

    public static readonly StyledProperty<IReadOnlyList<TimelineZoomRegion>?> ZoomRegionsProperty =
        AvaloniaProperty.Register<VideoTimelineControl, IReadOnlyList<TimelineZoomRegion>?>(nameof(ZoomRegions));

    public static readonly StyledProperty<IReadOnlyList<TimelineSpeedSegment>?> SpeedSegmentsProperty =
        AvaloniaProperty.Register<VideoTimelineControl, IReadOnlyList<TimelineSpeedSegment>?>(nameof(SpeedSegments));

    public static readonly StyledProperty<IReadOnlyList<double>?> ClickMarkersProperty =
        AvaloniaProperty.Register<VideoTimelineControl, IReadOnlyList<double>?>(nameof(ClickMarkers));

    public static readonly StyledProperty<int> SelectedSegmentProperty =
        AvaloniaProperty.Register<VideoTimelineControl, int>(nameof(SelectedSegment), -1, defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<double> InPointProperty =
        AvaloniaProperty.Register<VideoTimelineControl, double>(nameof(InPoint), 0d, defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<double> OutPointProperty =
        AvaloniaProperty.Register<VideoTimelineControl, double>(nameof(OutPoint), 1d, defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<double> PositionProperty =
        AvaloniaProperty.Register<VideoTimelineControl, double>(nameof(Position), 0d, defaultBindingMode: BindingMode.TwoWay);

    private const double HorizontalPadding = 12;
    private const double HandleWidth = 10;
    private const double MinimumSelectionSeconds = 0.05;
    private const double TrackTop = 16;
    private const double TrackHeight = 18;
    private const double SpeedLaneTop = 46;
    private const double SpeedLaneHeight = 22;
    private DragTarget _dragTarget;

    static VideoTimelineControl()
    {
        AffectsRender<VideoTimelineControl>(DurationProperty, InPointProperty, OutPointProperty, PositionProperty,
            ZoomRegionsProperty, SpeedSegmentsProperty, SelectedSegmentProperty, ClickMarkersProperty);
    }

    public VideoTimelineControl()
    {
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);
        MinHeight = 94;
    }

    public double Duration { get => GetValue(DurationProperty); set => SetValue(DurationProperty, value); }
    public IReadOnlyList<TimelineZoomRegion>? ZoomRegions { get => GetValue(ZoomRegionsProperty); set => SetValue(ZoomRegionsProperty, value); }
    public IReadOnlyList<TimelineSpeedSegment>? SpeedSegments { get => GetValue(SpeedSegmentsProperty); set => SetValue(SpeedSegmentsProperty, value); }
    public IReadOnlyList<double>? ClickMarkers { get => GetValue(ClickMarkersProperty); set => SetValue(ClickMarkersProperty, value); }
    public int SelectedSegment { get => GetValue(SelectedSegmentProperty); set => SetValue(SelectedSegmentProperty, value); }
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

        // Click markers show where the user actually acted, making it easy to scrub straight to the action.
        IReadOnlyList<double>? clicks = ClickMarkers;
        if (clicks != null && clicks.Count > 0)
        {
            IBrush clickBrush = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255));
            double markerY = track.Bottom - 4;
            foreach (double click in clicks)
            {
                double x = TimeToX(click, track);
                context.DrawEllipse(clickBrush, null, new Point(x, markerY), 1.7, 1.7);
            }
        }

        DrawSpeedLane(context, track, selection, foreground, muted);

        DrawHandle(context, inX, track, selection, foreground);
        DrawHandle(context, outX, track, selection, foreground);

        double positionX = TimeToX(Position, track);
        double playheadBottom = SpeedLaneTop + SpeedLaneHeight + 4;
        context.DrawLine(new Pen(foreground, 2), new Point(positionX, track.Top - 8), new Point(positionX, playheadBottom));
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
        context.DrawText(start, new Point(track.Left, playheadBottom + 3));
        context.DrawText(end, new Point(track.Right - end.Width, playheadBottom + 3));
    }

    private void DrawSpeedLane(DrawingContext context, Rect track, IBrush accent, IBrush foreground, IBrush muted)
    {
        IReadOnlyList<TimelineSpeedSegment>? segments = SpeedSegments;
        if (segments == null || segments.Count == 0) return;

        IBrush unityFill = GetBrush("ShareX.Brush.Control.Background", new SolidColorBrush(Color.Parse("#3A3A3A")));
        IBrush fasterFill = new SolidColorBrush(Color.FromArgb(235, 240, 138, 74));  // warm = sped up
        IBrush slowerFill = new SolidColorBrush(Color.FromArgb(235, 96, 165, 250));  // cool = slowed down
        IBrush removedFill = new SolidColorBrush(Color.FromArgb(150, 30, 30, 30));   // dark = cut out
        IBrush divider = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0));
        Pen selectedPen = new(foreground, 2);
        Pen removedPen = new(new SolidColorBrush(Color.FromArgb(140, 235, 90, 90)), 1);

        for (int i = 0; i < segments.Count; i++)
        {
            TimelineSpeedSegment segment = segments[i];
            double left = TimeToX(segment.Start, track);
            double right = TimeToX(segment.End, track);
            double width = Math.Max(1, right - left);
            Rect block = new(left, SpeedLaneTop, width, SpeedLaneHeight);

            bool unity = Math.Abs(segment.Speed - 1) < 1e-3;
            IBrush fill = segment.Removed ? removedFill : unity ? unityFill : (segment.Speed > 1 ? fasterFill : slowerFill);
            context.DrawRectangle(fill, null, block, 3, 3);

            if (segment.Removed)
            {
                // Diagonal strike-through so a cut clip is unmistakable even when it's tiny.
                context.DrawRectangle(null, removedPen, block.Deflate(1), 3, 3);
                context.DrawLine(removedPen, new Point(block.Left + 2, block.Bottom - 3), new Point(block.Right - 2, block.Top + 3));
            }

            if (i == SelectedSegment)
            {
                context.DrawRectangle(null, selectedPen, block.Deflate(1), 3, 3);
            }

            if (i > 0)
            {
                context.DrawLine(new Pen(divider, 1), new Point(left, SpeedLaneTop + 2), new Point(left, SpeedLaneTop + SpeedLaneHeight - 2));
            }

            if (!segment.Removed && (!unity || i == SelectedSegment))
            {
                FormattedText label = CreateText(FormatSpeed(segment.Speed), unity ? muted : Brushes.White);
                if (label.Width + 6 <= width)
                {
                    context.DrawText(label, new Point(left + (width - label.Width) / 2, SpeedLaneTop + (SpeedLaneHeight - label.Height) / 2));
                }
            }
        }
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

        // A click in the speed lane selects the segment under the cursor and parks the playhead there,
        // rather than scrubbing the trim track.
        if (point.Y >= SpeedLaneTop - 2 && point.Y <= SpeedLaneTop + SpeedLaneHeight + 2)
        {
            if (SelectSegmentAt(point, track))
            {
                e.Handled = true;
                return;
            }
        }

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

    private bool SelectSegmentAt(Point point, Rect track)
    {
        IReadOnlyList<TimelineSpeedSegment>? segments = SpeedSegments;
        if (segments == null || segments.Count == 0) return false;

        double time = XToTime(point.X, track);
        for (int i = 0; i < segments.Count; i++)
        {
            if (time >= segments[i].Start && time <= segments[i].End)
            {
                SetCurrentValue(SelectedSegmentProperty, i);
                SetCurrentValue(PositionProperty, Math.Clamp(time, InPoint, OutPoint));
                return true;
            }
        }
        return false;
    }

    private Rect GetTrackBounds() => new(HorizontalPadding, TrackTop, Math.Max(1, Bounds.Width - HorizontalPadding * 2), TrackHeight);
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

    private static string FormatSpeed(double speed) =>
        (speed % 1 == 0 ? speed.ToString("0") : speed.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)) + "×";

    private enum DragTarget
    {
        None,
        InPoint,
        OutPoint,
        Position
    }
}
