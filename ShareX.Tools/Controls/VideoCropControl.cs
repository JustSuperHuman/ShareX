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

    private const double HandleRadius = 6;
    private const double MinimumCropPixels = 16;
    private CropHandle _activeHandle;
    private Point _dragStartSource;
    private Rect _dragStartCrop;

    static VideoCropControl()
    {
        AffectsRender<VideoCropControl>(PreviewImageProperty, SourceWidthProperty, SourceHeightProperty, CropRectProperty);
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

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));

        if (PreviewImage == null || SourceWidth <= 0 || SourceHeight <= 0)
        {
            return;
        }

        Rect imageBounds = GetImageBounds();
        context.DrawImage(PreviewImage, new Rect(0, 0, PreviewImage.PixelSize.Width, PreviewImage.PixelSize.Height), imageBounds);

        Rect crop = SourceToDisplay(NormalizeCrop(CropRect), imageBounds);
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
        if (PreviewImage == null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
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
