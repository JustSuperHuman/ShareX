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
using Avalonia.Media.Imaging;

namespace ShareX.Tools.Controls;

public sealed class VideoCropControl : Control
{
    public static readonly StyledProperty<Bitmap?> PreviewImageProperty =
        AvaloniaProperty.Register<VideoCropControl, Bitmap?>(nameof(PreviewImage));

    public static readonly StyledProperty<int> SourceWidthProperty =
        AvaloniaProperty.Register<VideoCropControl, int>(nameof(SourceWidth));

    public static readonly StyledProperty<int> SourceHeightProperty =
        AvaloniaProperty.Register<VideoCropControl, int>(nameof(SourceHeight));

    public static readonly StyledProperty<Rect> CropRectProperty =
        AvaloniaProperty.Register<VideoCropControl, Rect>(nameof(CropRect), defaultBindingMode: BindingMode.TwoWay);

    // When set to a non-empty rectangle (in source pixels), the control renders that region scaled to
    // fill the view — a live WYSIWYG preview of the auto-zoom camera instead of the crop editor.
    public static readonly StyledProperty<Rect> CameraRectProperty =
        AvaloniaProperty.Register<VideoCropControl, Rect>(nameof(CameraRect));

    public static readonly StyledProperty<double> PreviewTimeProperty =
        AvaloniaProperty.Register<VideoCropControl, double>(nameof(PreviewTime));

    public static readonly StyledProperty<IReadOnlyList<VideoClickHighlight>?> ClickHighlightsProperty =
        AvaloniaProperty.Register<VideoCropControl, IReadOnlyList<VideoClickHighlight>?>(nameof(ClickHighlights));

    public static readonly StyledProperty<bool> ShowClickRipplesProperty =
        AvaloniaProperty.Register<VideoCropControl, bool>(nameof(ShowClickRipples));

    public static readonly StyledProperty<bool> ShowSpotlightProperty =
        AvaloniaProperty.Register<VideoCropControl, bool>(nameof(ShowSpotlight));

    public static readonly StyledProperty<Point> SpotlightCenterProperty =
        AvaloniaProperty.Register<VideoCropControl, Point>(nameof(SpotlightCenter), new Point(0.5, 0.5));

    public static readonly StyledProperty<double> SpotlightSizeProperty =
        AvaloniaProperty.Register<VideoCropControl, double>(nameof(SpotlightSize), 0.22);

    public static readonly StyledProperty<bool> ShowProgressBarProperty =
        AvaloniaProperty.Register<VideoCropControl, bool>(nameof(ShowProgressBar));

    public static readonly StyledProperty<double> ProgressFractionProperty =
        AvaloniaProperty.Register<VideoCropControl, double>(nameof(ProgressFraction));

    public static readonly StyledProperty<bool> ShowStudioBackgroundProperty =
        AvaloniaProperty.Register<VideoCropControl, bool>(nameof(ShowStudioBackground));

    public static readonly StyledProperty<Color> StudioTopColorProperty =
        AvaloniaProperty.Register<VideoCropControl, Color>(nameof(StudioTopColor), Color.FromRgb(49, 46, 129));

    public static readonly StyledProperty<Color> StudioBottomColorProperty =
        AvaloniaProperty.Register<VideoCropControl, Color>(nameof(StudioBottomColor), Color.FromRgb(15, 23, 42));

    private const double HandleRadius = 6;
    private const double MinimumCropPixels = 16;
    private CropHandle _activeHandle;
    private Point _dragStartSource;
    private Rect _dragStartCrop;

    static VideoCropControl()
    {
        AffectsRender<VideoCropControl>(PreviewImageProperty, SourceWidthProperty, SourceHeightProperty, CropRectProperty, CameraRectProperty,
            PreviewTimeProperty, ClickHighlightsProperty, ShowClickRipplesProperty,
            ShowSpotlightProperty, SpotlightCenterProperty, SpotlightSizeProperty,
            ShowProgressBarProperty, ProgressFractionProperty,
            ShowStudioBackgroundProperty, StudioTopColorProperty, StudioBottomColorProperty);
    }

    public VideoCropControl()
    {
        ClipToBounds = true;
        MinHeight = 260;
    }

    public Bitmap? PreviewImage { get => GetValue(PreviewImageProperty); set => SetValue(PreviewImageProperty, value); }
    public int SourceWidth { get => GetValue(SourceWidthProperty); set => SetValue(SourceWidthProperty, value); }
    public int SourceHeight { get => GetValue(SourceHeightProperty); set => SetValue(SourceHeightProperty, value); }
    public Rect CropRect { get => GetValue(CropRectProperty); set => SetValue(CropRectProperty, value); }
    public Rect CameraRect { get => GetValue(CameraRectProperty); set => SetValue(CameraRectProperty, value); }
    public double PreviewTime { get => GetValue(PreviewTimeProperty); set => SetValue(PreviewTimeProperty, value); }
    public IReadOnlyList<VideoClickHighlight>? ClickHighlights { get => GetValue(ClickHighlightsProperty); set => SetValue(ClickHighlightsProperty, value); }
    public bool ShowClickRipples { get => GetValue(ShowClickRipplesProperty); set => SetValue(ShowClickRipplesProperty, value); }
    public bool ShowSpotlight { get => GetValue(ShowSpotlightProperty); set => SetValue(ShowSpotlightProperty, value); }
    public Point SpotlightCenter { get => GetValue(SpotlightCenterProperty); set => SetValue(SpotlightCenterProperty, value); }
    public double SpotlightSize { get => GetValue(SpotlightSizeProperty); set => SetValue(SpotlightSizeProperty, value); }
    public bool ShowProgressBar { get => GetValue(ShowProgressBarProperty); set => SetValue(ShowProgressBarProperty, value); }
    public double ProgressFraction { get => GetValue(ProgressFractionProperty); set => SetValue(ProgressFractionProperty, value); }
    public bool ShowStudioBackground { get => GetValue(ShowStudioBackgroundProperty); set => SetValue(ShowStudioBackgroundProperty, value); }
    public Color StudioTopColor { get => GetValue(StudioTopColorProperty); set => SetValue(StudioTopColorProperty, value); }
    public Color StudioBottomColor { get => GetValue(StudioBottomColorProperty); set => SetValue(StudioBottomColorProperty, value); }

    private bool CameraActive => CameraRect.Width > 0 && CameraRect.Height > 0;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));

        if (PreviewImage == null || SourceWidth <= 0 || SourceHeight <= 0)
        {
            return;
        }

        if (CameraActive)
        {
            RenderCameraPreview(context);
            return;
        }

        Rect imageBounds = GetImageBounds();
        context.DrawImage(PreviewImage, new Rect(0, 0, PreviewImage.PixelSize.Width, PreviewImage.PixelSize.Height), imageBounds);

        Rect cropSource = NormalizeCrop(CropRect);
        Rect crop = SourceToDisplay(cropSource, imageBounds);

        // The exported frame is the crop region; the studio backdrop wraps it and insets the video, so
        // the cursor effects have to be mapped into the inset area to stay aligned.
        Rect effectsWindow = new(0, 0, SourceWidth, SourceHeight);
        Rect effectsRect = imageBounds;
        if (ShowStudioBackground)
        {
            effectsRect = DrawStudioPreview(context, cropSource, crop);
            effectsWindow = cropSource;
        }

        DrawSpotlight(context, effectsWindow, effectsRect);
        DrawClickRipples(context, effectsWindow, effectsRect);

        DrawProgressBar(context, crop);
        IBrush overlay = GetBrush("ShareX.Brush.Overlay.Modal", new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)));
        IBrush accent = GetBrush("ShareX.Brush.Accent.Start", new SolidColorBrush(Color.Parse("#3E83F2")));
        IBrush handle = GetBrush("ShareX.Brush.Text", Brushes.White);

        DrawOutsideOverlay(context, imageBounds, crop, overlay);
        context.DrawRectangle(null, new Pen(accent, 2), crop);

        IPen guidePen = new Pen(new SolidColorBrush(Color.FromArgb(145, 255, 255, 255)), 1);
        context.DrawLine(guidePen, new Point(crop.Left + crop.Width / 3, crop.Top), new Point(crop.Left + crop.Width / 3, crop.Bottom));
        context.DrawLine(guidePen, new Point(crop.Left + crop.Width * 2 / 3, crop.Top), new Point(crop.Left + crop.Width * 2 / 3, crop.Bottom));
        context.DrawLine(guidePen, new Point(crop.Left, crop.Top + crop.Height / 3), new Point(crop.Right, crop.Top + crop.Height / 3));
        context.DrawLine(guidePen, new Point(crop.Left, crop.Top + crop.Height * 2 / 3), new Point(crop.Right, crop.Top + crop.Height * 2 / 3));

        foreach (Point point in GetHandlePoints(crop))
        {
            context.DrawEllipse(handle, new Pen(accent, 2), point, HandleRadius, HandleRadius);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (PreviewImage == null || CameraActive || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        Point point = e.GetPosition(this);
        Rect imageBounds = GetImageBounds();
        _activeHandle = HitTestHandle(point, SourceToDisplay(NormalizeCrop(CropRect), imageBounds));
        if (_activeHandle == CropHandle.None)
        {
            return;
        }

        _dragStartSource = DisplayToSource(point, imageBounds);
        _dragStartCrop = NormalizeCrop(CropRect);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (CameraActive) return;
        Point point = e.GetPosition(this);
        Rect cropDisplay = SourceToDisplay(NormalizeCrop(CropRect), GetImageBounds());
        CropHandle hover = _activeHandle == CropHandle.None ? HitTestHandle(point, cropDisplay) : _activeHandle;
        Cursor = GetCursor(hover);

        if (_activeHandle != CropHandle.None)
        {
            UpdateCrop(DisplayToSource(point, GetImageBounds()));
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_activeHandle != CropHandle.None)
        {
            UpdateCrop(DisplayToSource(e.GetPosition(this), GetImageBounds()));
            _activeHandle = CropHandle.None;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    private void UpdateCrop(Point currentSource)
    {
        Vector delta = currentSource - _dragStartSource;
        Rect crop = _dragStartCrop;

        if (_activeHandle == CropHandle.Move)
        {
            crop = new Rect(crop.X + delta.X, crop.Y + delta.Y, crop.Width, crop.Height);
        }
        else
        {
            double left = crop.Left;
            double top = crop.Top;
            double right = crop.Right;
            double bottom = crop.Bottom;

            if (_activeHandle is CropHandle.TopLeft or CropHandle.Left or CropHandle.BottomLeft) left += delta.X;
            if (_activeHandle is CropHandle.TopRight or CropHandle.Right or CropHandle.BottomRight) right += delta.X;
            if (_activeHandle is CropHandle.TopLeft or CropHandle.Top or CropHandle.TopRight) top += delta.Y;
            if (_activeHandle is CropHandle.BottomLeft or CropHandle.Bottom or CropHandle.BottomRight) bottom += delta.Y;

            if (right - left < MinimumCropPixels)
            {
                if (_activeHandle is CropHandle.TopLeft or CropHandle.Left or CropHandle.BottomLeft) left = right - MinimumCropPixels;
                else right = left + MinimumCropPixels;
            }
            if (bottom - top < MinimumCropPixels)
            {
                if (_activeHandle is CropHandle.TopLeft or CropHandle.Top or CropHandle.TopRight) top = bottom - MinimumCropPixels;
                else bottom = top + MinimumCropPixels;
            }

            crop = new Rect(left, top, right - left, bottom - top);
        }

        SetCurrentValue(CropRectProperty, NormalizeCrop(crop));
    }

    private Rect NormalizeCrop(Rect crop)
    {
        double width = Math.Clamp(crop.Width <= 0 ? SourceWidth : crop.Width, MinimumCropPixels, Math.Max(MinimumCropPixels, SourceWidth));
        double height = Math.Clamp(crop.Height <= 0 ? SourceHeight : crop.Height, MinimumCropPixels, Math.Max(MinimumCropPixels, SourceHeight));
        double x = Math.Clamp(crop.X, 0, Math.Max(0, SourceWidth - width));
        double y = Math.Clamp(crop.Y, 0, Math.Max(0, SourceHeight - height));
        return new Rect(Math.Round(x), Math.Round(y), Math.Round(width), Math.Round(height));
    }

    private void RenderCameraPreview(DrawingContext context)
    {
        if (PreviewImage == null) return;

        // Map the camera window (source pixels) into the preview bitmap's own pixel space, which may be
        // downscaled relative to the source during playback.
        double bitmapScaleX = PreviewImage.PixelSize.Width / (double)SourceWidth;
        double bitmapScaleY = PreviewImage.PixelSize.Height / (double)SourceHeight;
        Rect sourceRect = new(
            CameraRect.X * bitmapScaleX,
            CameraRect.Y * bitmapScaleY,
            CameraRect.Width * bitmapScaleX,
            CameraRect.Height * bitmapScaleY);

        double aspect = CameraRect.Width / CameraRect.Height;
        double width = Bounds.Width;
        double height = width / aspect;
        if (height > Bounds.Height)
        {
            height = Bounds.Height;
            width = height * aspect;
        }
        Rect dest = new((Bounds.Width - width) / 2, (Bounds.Height - height) / 2, width, height);

        Rect effectsRect = dest;
        if (ShowStudioBackground)
        {
            effectsRect = DrawStudioPreview(context, CameraRect, dest);
        }
        else
        {
            context.DrawImage(PreviewImage, sourceRect, dest);
        }

        DrawSpotlight(context, CameraRect, effectsRect);
        DrawClickRipples(context, CameraRect, effectsRect);
        DrawProgressBar(context, dest);
    }

    // WYSIWYG approximation of the export's studio background: the frame area fills with the gradient
    // backdrop and the video re-renders inset with rounded corners and a simple shadow. Returns the
    // inset rectangle the video now occupies so later effect layers can map into it.
    private Rect DrawStudioPreview(DrawingContext context, Rect sourceWindow, Rect frameRect)
    {
        const double inset = 0.88; // keep in sync with VideoEffectsGraph.StudioInset

        LinearGradientBrush gradient = new()
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(StudioTopColor, 0),
                new GradientStop(StudioBottomColor, 1)
            }
        };
        context.FillRectangle(gradient, frameRect);

        double videoWidth = frameRect.Width * inset;
        double videoHeight = frameRect.Height * inset;
        Rect video = new(frameRect.X + (frameRect.Width - videoWidth) / 2, frameRect.Y + (frameRect.Height - videoHeight) / 2, videoWidth, videoHeight);
        double radius = Math.Max(3, Math.Min(videoWidth, videoHeight) * 0.03);

        Rect shadow = video.Translate(new Vector(0, videoHeight * 0.025)).Inflate(radius * 0.6);
        context.DrawRectangle(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), null, shadow, radius * 2, radius * 2);

        if (PreviewImage != null && sourceWindow.Width > 0 && sourceWindow.Height > 0)
        {
            double bitmapScaleX = PreviewImage.PixelSize.Width / (double)Math.Max(1, SourceWidth);
            double bitmapScaleY = PreviewImage.PixelSize.Height / (double)Math.Max(1, SourceHeight);
            Rect bitmapSource = new(
                sourceWindow.X * bitmapScaleX,
                sourceWindow.Y * bitmapScaleY,
                sourceWindow.Width * bitmapScaleX,
                sourceWindow.Height * bitmapScaleY);

            using DrawingContext.PushedState clip = context.PushClip(new RoundedRect(video, radius));
            context.DrawImage(PreviewImage, bitmapSource, video);
        }

        return video;
    }

    // Mirrors the export's bottom progress bar over whichever rectangle stands in for the output frame.
    // A faint track is always drawn so enabling the effect is visible even with the playhead at 0.
    private void DrawProgressBar(DrawingContext context, Rect frame)
    {
        if (!ShowProgressBar || frame.Width <= 0) return;

        double barHeight = Math.Max(3, frame.Height / 72);
        Rect track = new(frame.X, frame.Bottom - barHeight, frame.Width, barHeight);
        context.FillRectangle(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), track);

        double width = frame.Width * Math.Clamp(ProgressFraction, 0, 1);
        if (width <= 0) return;

        LinearGradientBrush brush = new()
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(230, 62, 131, 242), 0),
                new GradientStop(Color.FromArgb(230, 111, 76, 255), 1)
            }
        };
        context.FillRectangle(brush, new Rect(frame.X, frame.Bottom - barHeight, width, barHeight));
    }

    // Approximates the export's cursor spotlight: everything outside a circle around the cursor is
    // dimmed (the export adds a soft edge the preview skips).
    private void DrawSpotlight(DrawingContext context, Rect sourceWindow, Rect displayRect)
    {
        if (!ShowSpotlight || sourceWindow.Width <= 0 || SourceWidth <= 0) return;

        double scale = displayRect.Width / sourceWindow.Width;
        Point center = new(
            displayRect.X + (SpotlightCenter.X * SourceWidth - sourceWindow.X) * scale,
            displayRect.Y + (SpotlightCenter.Y * SourceHeight - sourceWindow.Y) * (displayRect.Height / sourceWindow.Height));
        double radius = Math.Min(SourceWidth, SourceHeight) * Math.Clamp(SpotlightSize, 0.05, 0.5) * scale;

        GeometryGroup dimShape = new()
        {
            FillRule = FillRule.EvenOdd,
            Children =
            {
                new RectangleGeometry(displayRect),
                new EllipseGeometry { Center = center, RadiusX = radius, RadiusY = radius }
            }
        };

        using DrawingContext.PushedState clip = context.PushClip(displayRect);
        context.DrawGeometry(new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)), null, dimShape);
    }

    // Draws the same click pulse the export renders, mapped from source pixels through whichever view
    // (full frame or auto-zoom camera window) is on screen.
    private void DrawClickRipples(DrawingContext context, Rect sourceWindow, Rect displayRect)
    {
        if (!ShowClickRipples || ClickHighlights is not { Count: > 0 } clicks || sourceWindow.Width <= 0) return;

        const double rippleSeconds = 0.42;
        double time = PreviewTime;
        double scale = displayRect.Width / sourceWindow.Width;
        double canvas = Math.Max(28, Math.Min(SourceWidth, SourceHeight) * 0.13);

        using DrawingContext.PushedState clip = context.PushClip(displayRect);
        foreach (VideoClickHighlight click in clicks)
        {
            double age = time - click.Time;
            if (age < 0 || age > rippleSeconds) continue;

            double progress = age / rippleSeconds;
            double sourceX = click.X * SourceWidth;
            double sourceY = click.Y * SourceHeight;
            Point center = new(
                displayRect.X + (sourceX - sourceWindow.X) * scale,
                displayRect.Y + (sourceY - sourceWindow.Y) * (displayRect.Height / sourceWindow.Height));

            double radius = (0.18 + 0.30 * progress) * canvas * scale;
            byte alpha = (byte)(Math.Clamp(1 - progress, 0, 1) * 200);
            Pen ring = new(new SolidColorBrush(Color.FromArgb(alpha, 255, 202, 87)), Math.Max(1.5, 2.5 * scale));
            IBrush fill = new SolidColorBrush(Color.FromArgb((byte)(alpha / 4), 255, 202, 87));
            context.DrawEllipse(fill, ring, center, radius, radius);
        }
    }

    private Rect GetImageBounds()
    {
        double scale = Math.Min(Bounds.Width / Math.Max(1, SourceWidth), Bounds.Height / Math.Max(1, SourceHeight));
        double width = SourceWidth * scale;
        double height = SourceHeight * scale;
        return new Rect((Bounds.Width - width) / 2, (Bounds.Height - height) / 2, width, height);
    }

    private Rect SourceToDisplay(Rect source, Rect imageBounds) => new(
        imageBounds.X + source.X / SourceWidth * imageBounds.Width,
        imageBounds.Y + source.Y / SourceHeight * imageBounds.Height,
        source.Width / SourceWidth * imageBounds.Width,
        source.Height / SourceHeight * imageBounds.Height);

    private Point DisplayToSource(Point display, Rect imageBounds) => new(
        Math.Clamp((display.X - imageBounds.X) / imageBounds.Width * SourceWidth, 0, SourceWidth),
        Math.Clamp((display.Y - imageBounds.Y) / imageBounds.Height * SourceHeight, 0, SourceHeight));

    private static void DrawOutsideOverlay(DrawingContext context, Rect image, Rect crop, IBrush overlay)
    {
        context.FillRectangle(overlay, new Rect(image.Left, image.Top, image.Width, Math.Max(0, crop.Top - image.Top)));
        context.FillRectangle(overlay, new Rect(image.Left, crop.Bottom, image.Width, Math.Max(0, image.Bottom - crop.Bottom)));
        context.FillRectangle(overlay, new Rect(image.Left, crop.Top, Math.Max(0, crop.Left - image.Left), crop.Height));
        context.FillRectangle(overlay, new Rect(crop.Right, crop.Top, Math.Max(0, image.Right - crop.Right), crop.Height));
    }

    private static IEnumerable<Point> GetHandlePoints(Rect crop)
    {
        yield return crop.TopLeft;
        yield return new Point(crop.Center.X, crop.Top);
        yield return crop.TopRight;
        yield return new Point(crop.Left, crop.Center.Y);
        yield return new Point(crop.Right, crop.Center.Y);
        yield return crop.BottomLeft;
        yield return new Point(crop.Center.X, crop.Bottom);
        yield return crop.BottomRight;
    }

    private static CropHandle HitTestHandle(Point point, Rect crop)
    {
        (CropHandle Handle, Point Point)[] handles =
        [
            (CropHandle.TopLeft, crop.TopLeft),
            (CropHandle.Top, new Point(crop.Center.X, crop.Top)),
            (CropHandle.TopRight, crop.TopRight),
            (CropHandle.Left, new Point(crop.Left, crop.Center.Y)),
            (CropHandle.Right, new Point(crop.Right, crop.Center.Y)),
            (CropHandle.BottomLeft, crop.BottomLeft),
            (CropHandle.Bottom, new Point(crop.Center.X, crop.Bottom)),
            (CropHandle.BottomRight, crop.BottomRight)
        ];

        foreach ((CropHandle handle, Point handlePoint) in handles)
        {
            double distance = Math.Sqrt(Math.Pow(handlePoint.X - point.X, 2) + Math.Pow(handlePoint.Y - point.Y, 2));
            if (distance <= HandleRadius + 5)
            {
                return handle;
            }
        }

        return crop.Contains(point) ? CropHandle.Move : CropHandle.None;
    }

    private static Cursor? GetCursor(CropHandle handle) => handle switch
    {
        CropHandle.Move => new Cursor(StandardCursorType.SizeAll),
        CropHandle.Left or CropHandle.Right => new Cursor(StandardCursorType.SizeWestEast),
        CropHandle.Top or CropHandle.Bottom => new Cursor(StandardCursorType.SizeNorthSouth),
        CropHandle.TopLeft or CropHandle.BottomRight => new Cursor(StandardCursorType.TopLeftCorner),
        CropHandle.TopRight or CropHandle.BottomLeft => new Cursor(StandardCursorType.TopRightCorner),
        _ => null
    };

    private IBrush GetBrush(string key, IBrush fallback) =>
        Application.Current?.TryGetResource(key, ActualThemeVariant, out object? value) == true && value is IBrush brush ? brush : fallback;

    private enum CropHandle
    {
        None,
        Move,
        TopLeft,
        Top,
        TopRight,
        Left,
        Right,
        BottomLeft,
        Bottom,
        BottomRight
    }
}
