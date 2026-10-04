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
using Avalonia.Media.Imaging;
using ShareX.HelpersLib;
using SkiaSharp;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace ShareX.Tools;

// A rendered cursor image sized in source-video pixels. The hotspot is the sprite pixel that sits on
// the recorded cursor position.
public sealed record VideoCursorSprite(byte[] Png, int Width, int Height, double HotspotX, double HotspotY);

public sealed record VideoCursorStyleItem(string Id, string DisplayName)
{
    public Bitmap Preview { get; } = VideoCursorArt.CreatePreview(Id);
}

// Draws the replacement cursors. Everything except the pizza is vector art rendered at the exact size
// the video needs, so cursors stay crisp at any resolution and the preview matches the export.
public static class VideoCursorArt
{
    public const string DefaultStyleId = "Classic";

    public static IReadOnlyList<(string Id, string DisplayName)> Styles { get; } =
    [
        ("Classic", "Classic"),
        ("Snow", "Snow"),
        ("Pizza", "Pizza"),
        ("Neon", "Neon"),
        ("Sunny", "Sunny"),
        ("Halo", "Halo"),
        ("Laser", "Laser")
    ];

    private const string PizzaResourceName = "ShareX.Tools.VideoEditor.Cursors.pizza.cur";

    // The pizza file carries no usable hotspot, so point with the tip of the slice.
    private static readonly SKPoint PizzaHotspot = new(34, 19);

    private static readonly Lazy<SKBitmap?> PizzaBitmap = new(LoadPizza);

    // Nominal cursor height in source pixels for a size multiplier of 1 — roughly a system arrow.
    public static int GetSpriteSize(int sourceWidth, int sourceHeight, double size)
    {
        double basis = Math.Min(sourceWidth, sourceHeight) * 0.032;
        return (int)Math.Clamp(Math.Round(basis * size), 12, 480);
    }

    public static VideoCursorSprite Render(string? styleId, int size)
    {
        size = Math.Clamp(size, 8, 480);

        return styleId switch
        {
            "Snow" => RenderArrow(size, [new SKColor(255, 255, 255)], new SKColor(24, 24, 27), null),
            "Pizza" => RenderPizza(size) ?? RenderClassic(size),
            "Neon" => RenderArrow(size, [new SKColor(34, 211, 238), new SKColor(124, 58, 237)], new SKColor(255, 255, 255), new SKColor(99, 102, 241, 190)),
            "Sunny" => RenderArrow(size, [new SKColor(253, 224, 71), new SKColor(251, 146, 60)], new SKColor(24, 24, 27), null),
            "Halo" => RenderHalo(size),
            "Laser" => RenderLaser(size),
            _ => RenderClassic(size)
        };
    }

    public static Bitmap CreatePreview(string styleId)
    {
        VideoCursorSprite sprite = Render(styleId, 56);
        return new Bitmap(new MemoryStream(sprite.Png, writable: false));
    }

    public static string WriteTempPng(VideoCursorSprite sprite)
    {
        string path = Path.Combine(Path.GetTempPath(), $"ShareX-videoedit-fx-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, sprite.Png);
        return path;
    }

    private static VideoCursorSprite RenderClassic(int size) =>
        RenderArrow(size, [new SKColor(24, 24, 27)], new SKColor(255, 255, 255), null);

    // The pointer arrow, tip at the origin: an outline band, a fill (flat or diagonal gradient), and a
    // soft drop shadow — or a colored glow when one is given.
    private static VideoCursorSprite RenderArrow(int size, SKColor[] fill, SKColor outline, SKColor? glow)
    {
        float scale = size / 0.88f;
        float stroke = Math.Max(1.5f, size * 0.07f);
        float pad = MathF.Ceiling(size * 0.2f + stroke);
        int width = (int)MathF.Ceiling(0.5f * scale + pad * 2);
        int height = (int)MathF.Ceiling(size + pad * 2);

        using SKSurface surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        SKCanvas canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Translate(pad, pad);

        using SKPath path = new();
        path.MoveTo(0, 0);
        path.LineTo(0, 0.74f * scale);
        path.LineTo(0.17f * scale, 0.585f * scale);
        path.LineTo(0.30f * scale, 0.88f * scale);
        path.LineTo(0.41f * scale, 0.83f * scale);
        path.LineTo(0.285f * scale, 0.545f * scale);
        path.LineTo(0.50f * scale, 0.545f * scale);
        path.Close();

        using (SKPaint shadow = new())
        {
            shadow.IsAntialias = true;
            shadow.Style = SKPaintStyle.StrokeAndFill;
            shadow.StrokeWidth = stroke * 2;
            shadow.StrokeJoin = SKStrokeJoin.Round;
            shadow.Color = glow ?? new SKColor(0, 0, 0, 105);
            shadow.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, size * (glow == null ? 0.055f : 0.075f));

            canvas.Save();
            if (glow == null) canvas.Translate(0, size * 0.05f);
            canvas.DrawPath(path, shadow);
            canvas.Restore();
        }

        using (SKPaint outlinePaint = new())
        {
            outlinePaint.IsAntialias = true;
            outlinePaint.Style = SKPaintStyle.StrokeAndFill;
            outlinePaint.StrokeWidth = stroke * 2;
            outlinePaint.StrokeJoin = SKStrokeJoin.Round;
            outlinePaint.Color = outline;
            canvas.DrawPath(path, outlinePaint);
        }

        using (SKPaint fillPaint = new())
        {
            fillPaint.IsAntialias = true;
            fillPaint.Style = SKPaintStyle.StrokeAndFill;
            fillPaint.StrokeWidth = stroke * 0.35f;
            fillPaint.StrokeJoin = SKStrokeJoin.Round;
            fillPaint.Color = fill[0];
            using SKShader? shader = fill.Length > 1
                ? SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0.4f * scale, 0.88f * scale), fill, SKShaderTileMode.Clamp)
                : null;
            fillPaint.Shader = shader;
            canvas.DrawPath(path, fillPaint);
        }

        // The outline rounds off the tip, so the visual point sits a little up and left of the path's.
        return Encode(surface, width, height, pad - stroke * 0.5, pad - stroke * 0.8);
    }

    // A translucent touch-style ring centered on the cursor — points without covering what's under it.
    private static VideoCursorSprite RenderHalo(int size)
    {
        float radius = size * 0.5f;
        float ring = Math.Max(2f, size * 0.07f);
        float pad = MathF.Ceiling(ring + 2);
        int dimension = (int)MathF.Ceiling((radius + pad) * 2);
        float center = dimension / 2f;

        using SKSurface surface = SKSurface.Create(new SKImageInfo(dimension, dimension, SKColorType.Rgba8888, SKAlphaType.Premul));
        SKCanvas canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        using SKPaint paint = new();
        paint.IsAntialias = true;

        paint.Style = SKPaintStyle.Fill;
        paint.Color = new SKColor(255, 202, 87, 80);
        canvas.DrawCircle(center, center, radius, paint);

        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = ring;
        paint.Color = new SKColor(255, 202, 87, 240);
        canvas.DrawCircle(center, center, radius - ring / 2, paint);

        paint.Style = SKPaintStyle.Fill;
        paint.Color = new SKColor(24, 24, 27, 200);
        canvas.DrawCircle(center, center, size * 0.11f, paint);
        paint.Color = new SKColor(255, 255, 255);
        canvas.DrawCircle(center, center, size * 0.075f, paint);

        return Encode(surface, dimension, dimension, center, center);
    }

    // A presenter's laser dot: white-hot core fading through red into a soft glow.
    private static VideoCursorSprite RenderLaser(int size)
    {
        float radius = size * 0.6f;
        int dimension = (int)MathF.Ceiling(radius * 2) + 2;
        float center = dimension / 2f;

        using SKSurface surface = SKSurface.Create(new SKImageInfo(dimension, dimension, SKColorType.Rgba8888, SKAlphaType.Premul));
        SKCanvas canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        using SKShader shader = SKShader.CreateRadialGradient(
            new SKPoint(center, center),
            radius,
            [new SKColor(255, 255, 255), new SKColor(255, 236, 236), new SKColor(255, 45, 55), new SKColor(255, 45, 55, 110), new SKColor(255, 45, 55, 0)],
            [0f, 0.14f, 0.34f, 0.55f, 1f],
            SKShaderTileMode.Clamp);
        using SKPaint paint = new();
        paint.IsAntialias = true;
        paint.Shader = shader;
        canvas.DrawCircle(center, center, radius, paint);

        return Encode(surface, dimension, dimension, center, center);
    }

    private static VideoCursorSprite? RenderPizza(int size)
    {
        SKBitmap? pizza = PizzaBitmap.Value;
        if (pizza == null) return null;

        // Illustrated cursors read smaller than an arrow at the same height, so give the slice more room.
        float scale = size * 1.7f / pizza.Height;
        float blur = Math.Max(1f, size * 0.05f);
        float pad = MathF.Ceiling(blur * 3);
        int width = (int)MathF.Ceiling(pizza.Width * scale + pad * 2);
        int height = (int)MathF.Ceiling(pizza.Height * scale + pad * 2);

        using SKSurface surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        SKCanvas canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        using SKImage image = SKImage.FromBitmap(pizza);
        using SKImageFilter shadow = SKImageFilter.CreateDropShadow(0, size * 0.05f, blur, blur, new SKColor(0, 0, 0, 110));
        using SKPaint paint = new();
        paint.IsAntialias = true;
        paint.ImageFilter = shadow;

        SKRect destination = new(pad, pad, pad + pizza.Width * scale, pad + pizza.Height * scale);
        canvas.DrawImage(image, destination, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);

        return Encode(surface, width, height, pad + PizzaHotspot.X * scale, pad + PizzaHotspot.Y * scale);
    }

    private static VideoCursorSprite Encode(SKSurface surface, int width, int height, double hotspotX, double hotspotY)
    {
        using SKImage image = surface.Snapshot();
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return new VideoCursorSprite(data.ToArray(), width, height, hotspotX, hotspotY);
    }

    private static SKBitmap? LoadPizza()
    {
        try
        {
            using Stream? stream = typeof(VideoCursorArt).Assembly.GetManifestResourceStream(PizzaResourceName);
            if (stream == null) return null;

            using MemoryStream memory = new();
            stream.CopyTo(memory);
            return DecodeCursorFile(memory.ToArray());
        }
        catch (Exception e)
        {
            DebugHelper.WriteException(e);
            return null;
        }
    }

    // Reads the first image of a .cur file (or an icon saved with that extension, which is what the
    // pizza is): either an embedded PNG or a 32-bit bottom-up DIB whose height field counts the
    // (ignored) AND mask as well.
    private static SKBitmap? DecodeCursorFile(byte[] data)
    {
        if (data.Length < 22 || BitConverter.ToUInt16(data, 2) is not (1 or 2) || BitConverter.ToUInt16(data, 4) == 0) return null;

        int length = BitConverter.ToInt32(data, 14);
        int offset = BitConverter.ToInt32(data, 18);
        if (offset < 22 || length <= 0 || offset + length > data.Length) return null;

        if (data.AsSpan(offset, 4).SequenceEqual<byte>([0x89, 0x50, 0x4E, 0x47]))
        {
            return SKBitmap.Decode(data.AsSpan(offset, length));
        }

        int headerSize = BitConverter.ToInt32(data, offset);
        int width = BitConverter.ToInt32(data, offset + 4);
        int height = Math.Abs(BitConverter.ToInt32(data, offset + 8)) / 2;
        int bitsPerPixel = BitConverter.ToUInt16(data, offset + 14);
        int stride = width * 4;
        int pixels = offset + headerSize;
        if (bitsPerPixel != 32 || width <= 0 || height <= 0 || pixels + stride * height > data.Length) return null;

        SKBitmap bitmap = new(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        for (int y = 0; y < height; y++)
        {
            Marshal.Copy(data, pixels + (height - 1 - y) * stride, bitmap.GetPixels() + y * bitmap.RowBytes, stride);
        }
        return bitmap;
    }
}

// The cursor's route through the recording after smoothing, sampled densely on a uniform clock.
// Smoothing is zero-phase (run forwards and backwards), so the cursor glides without trailing behind
// what happens on screen, and it is pinned back to the true position around every click so clicks
// always land exactly on their target.
public sealed class VideoCursorPath
{
    public const double SampleRate = 60;

    private const double ClickPinSeconds = 0.08;

    private readonly double[] _x;
    private readonly double[] _y;

    private VideoCursorPath(double[] x, double[] y)
    {
        _x = x;
        _y = y;
    }

    public static VideoCursorPath? Build(ScreenRecordingMotionData? data, double smoothing)
    {
        if (data is not { HasSamples: true }) return null;

        double duration = Math.Max(data.Duration, data.Samples[^1].T);
        if (duration <= 0) return null;

        int n = Math.Clamp((int)Math.Ceiling(duration * SampleRate) + 1, 2, 2_000_000);
        double[] rawX = new double[n];
        double[] rawY = new double[n];
        Resample(data.Samples, n, rawX, rawY);

        smoothing = Math.Clamp(smoothing, 0, 1);
        if (smoothing < 0.01) return new VideoCursorPath(rawX, rawY);

        double tau = 0.02 + 0.14 * smoothing;
        double[] x = SmoothZeroPhase(rawX, tau);
        double[] y = SmoothZeroPhase(rawY, tau);

        if (data.Clicks != null)
        {
            int reach = (int)Math.Ceiling(ClickPinSeconds * 3 * SampleRate);
            double[] pin = new double[n];
            foreach (ScreenRecordingMotionClick click in data.Clicks)
            {
                int center = (int)Math.Round(click.T * SampleRate);
                for (int i = Math.Max(0, center - reach); i <= Math.Min(n - 1, center + reach); i++)
                {
                    double d = i / SampleRate - click.T;
                    double weight = Math.Exp(-(d * d) / (2 * ClickPinSeconds * ClickPinSeconds));
                    if (weight > pin[i]) pin[i] = weight;
                }
            }

            for (int i = 0; i < n; i++)
            {
                if (pin[i] <= 0) continue;
                x[i] += (rawX[i] - x[i]) * pin[i];
                y[i] += (rawY[i] - y[i]) * pin[i];
            }
        }

        return new VideoCursorPath(x, y);
    }

    // Normalized cursor position at an absolute recording time.
    public (double X, double Y) Sample(double time)
    {
        double position = Math.Clamp(time * SampleRate, 0, _x.Length - 1);
        int index = Math.Min((int)position, _x.Length - 2);
        double f = position - index;
        return (_x[index] + (_x[index + 1] - _x[index]) * f, _y[index] + (_y[index + 1] - _y[index]) * f);
    }

    // The path across [inPoint, inPoint + length] as keyframes in source pixels on the trimmed clip's
    // clock, thinned until straight-line interpolation stays within <tolerance> pixels of the real path.
    public List<(double T, double X, double Y)> GetKeyframes(
        double inPoint, double length, int sourceWidth, int sourceHeight, double tolerance = 0.35, int maxPoints = 3000)
    {
        if (length <= 0) return [];

        int n = Math.Max(2, (int)Math.Ceiling(length * SampleRate) + 1);
        double step = length / (n - 1);
        double[] t = new double[n];
        double[] x = new double[n];
        double[] y = new double[n];
        for (int i = 0; i < n; i++)
        {
            t[i] = i * step;
            (double sx, double sy) = Sample(inPoint + t[i]);
            x[i] = sx * sourceWidth;
            y[i] = sy * sourceHeight;
        }

        while (true)
        {
            bool[] keep = Simplify(t, x, y, tolerance);
            int count = keep.Count(k => k);
            if (count <= maxPoints || tolerance > 64)
            {
                List<(double, double, double)> result = new(count);
                for (int i = 0; i < n; i++)
                {
                    if (keep[i]) result.Add((t[i], x[i], y[i]));
                }
                return result;
            }
            tolerance *= 1.5;
        }
    }

    private static void Resample(IReadOnlyList<ScreenRecordingMotionSample> samples, int n, double[] x, double[] y)
    {
        int j = 0;
        for (int i = 0; i < n; i++)
        {
            double time = i / SampleRate;
            while (j < samples.Count - 1 && samples[j + 1].T <= time) j++;

            if (j >= samples.Count - 1)
            {
                x[i] = samples[^1].X;
                y[i] = samples[^1].Y;
                continue;
            }

            ScreenRecordingMotionSample a = samples[j];
            ScreenRecordingMotionSample b = samples[j + 1];
            double span = b.T - a.T;
            double f = span > 1e-6 ? Math.Clamp((time - a.T) / span, 0, 1) : 0;
            x[i] = a.X + (b.X - a.X) * f;
            y[i] = a.Y + (b.Y - a.Y) * f;
        }
    }

    private static double[] SmoothZeroPhase(double[] input, double tau)
    {
        int n = input.Length;
        double[] output = new double[n];
        double alpha = 1 - Math.Exp(-1 / (SampleRate * tau));

        output[0] = input[0];
        for (int i = 1; i < n; i++) output[i] = output[i - 1] + alpha * (input[i] - output[i - 1]);
        for (int i = n - 2; i >= 0; i--) output[i] = output[i + 1] + alpha * (output[i] - output[i + 1]);
        return output;
    }

    // Douglas-Peucker against time-linear interpolation, with an explicit stack so long recordings
    // can't overflow the call stack.
    private static bool[] Simplify(double[] t, double[] x, double[] y, double tolerance)
    {
        int n = t.Length;
        bool[] keep = new bool[n];
        keep[0] = keep[n - 1] = true;

        Stack<(int Lo, int Hi)> pending = new();
        pending.Push((0, n - 1));
        while (pending.Count > 0)
        {
            (int lo, int hi) = pending.Pop();
            if (hi <= lo + 1) continue;

            double span = Math.Max(1e-9, t[hi] - t[lo]);
            double maxError = 0;
            int maxIndex = -1;
            for (int i = lo + 1; i < hi; i++)
            {
                double f = (t[i] - t[lo]) / span;
                double ex = x[i] - (x[lo] + (x[hi] - x[lo]) * f);
                double ey = y[i] - (y[lo] + (y[hi] - y[lo]) * f);
                double error = ex * ex + ey * ey;
                if (error > maxError)
                {
                    maxError = error;
                    maxIndex = i;
                }
            }

            if (maxIndex > 0 && maxError > tolerance * tolerance)
            {
                keep[maxIndex] = true;
                pending.Push((lo, maxIndex));
                pending.Push((maxIndex, hi));
            }
        }

        return keep;
    }
}

// Builds the ffmpeg statements that draw the cursor sprite along its smoothed path.
public static class VideoCursorGraph
{
    public static string AddCursor(
        ExportGraphBuilder graph,
        string inputLabel,
        VideoCursorPath path,
        VideoCursorSprite sprite,
        double inPoint,
        double length,
        int sourceWidth,
        int sourceHeight,
        Rect crop)
    {
        List<(double T, double X, double Y)> keyframes = path.GetKeyframes(inPoint, length, sourceWidth, sourceHeight);
        if (keyframes.Count < 2) return inputLabel;

        double[] times = new double[keyframes.Count];
        double[] xs = new double[keyframes.Count];
        double[] ys = new double[keyframes.Count];
        for (int i = 0; i < keyframes.Count; i++)
        {
            times[i] = keyframes[i].T;
            xs[i] = keyframes[i].X - crop.X - sprite.HotspotX;
            ys[i] = keyframes[i].Y - crop.Y - sprite.HotspotY;
        }

        int spriteIndex = graph.AddImageInput(VideoCursorArt.WriteTempPng(sprite));
        string outLabel = graph.NewLabel();
        // yuv444 keeps the sprite on whole-pixel positions; the default 4:2:0 blend snaps to every
        // second pixel, which shows as stutter on slow moves.
        graph.Parts.Add($"{inputLabel}[{spriteIndex}:v]overlay=x='{BuildPathExpr(times, xs)}':y='{BuildPathExpr(times, ys)}':format=yuv444{outLabel}");
        return outLabel;
    }

    // Piecewise-linear value of t as a binary search over the keyframe times. Unlike a running sum of
    // ramps this stays cheap to evaluate with thousands of keyframes, which a cursor path needs.
    public static string BuildPathExpr(double[] times, double[] values)
    {
        StringBuilder sb = new();
        AppendRange(sb, times, values, 0, values.Length - 1);
        return sb.ToString();
    }

    private static void AppendRange(StringBuilder sb, double[] times, double[] values, int lo, int hi)
    {
        if (hi - lo <= 1)
        {
            double span = times[hi] - times[lo];
            double slope = span > 1e-6 ? (values[hi] - values[lo]) / span : 0;
            sb.Append(Num(values[lo], "0.##"));
            if (Math.Abs(slope) >= 0.0005)
            {
                sb.Append(slope >= 0 ? '+' : '-');
                sb.Append(Num(Math.Abs(slope), "0.###"));
                sb.Append("*(t-");
                sb.Append(Num(times[lo], "0.####"));
                sb.Append(')');
            }
            return;
        }

        int mid = (lo + hi) / 2;
        sb.Append("if(lt(t,");
        sb.Append(Num(times[mid], "0.####"));
        sb.Append("),");
        AppendRange(sb, times, values, lo, mid);
        sb.Append(',');
        AppendRange(sb, times, values, mid, hi);
        sb.Append(')');
    }

    private static string Num(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);
}

// Renders the smooth cursor straight into a finished recording, for recordings that skip the editor.
public static class VideoCursorBaker
{
    public static VideoEditorExportRequest? BuildRequest(
        string inputFilePath,
        string outputFilePath,
        ScreenRecordingMotionData motion,
        VideoEditorOptions options,
        int width,
        int height,
        double duration)
    {
        VideoCursorPath? path = VideoCursorPath.Build(motion, options.CursorSmoothing);
        if (path == null || width <= 0 || height <= 0 || duration <= 0) return null;

        VideoCursorSprite sprite = VideoCursorArt.Render(options.CursorStyle, VideoCursorArt.GetSpriteSize(width, height, options.CursorSize));
        ExportGraphBuilder graph = new();
        string label = VideoCursorGraph.AddCursor(graph, "[0:v]", path, sprite, 0, duration, width, height, new Rect(0, 0, width, height));
        if (!graph.HasFilters) return null;

        string graphFile = Path.Combine(Path.GetTempPath(), $"ShareX-videoedit-{Guid.NewGuid():N}.txt");
        File.WriteAllText(graphFile, graph.BuildGraph(label));

        string arguments =
            $"-i \"{inputFilePath}\" {string.Join(" ", graph.ExtraInputs)} -filter_complex_script \"{graphFile}\" " +
            "-map \"[v]\" -map 0:a? -c:a copy -c:v libx264 -preset veryfast -crf 18 -pix_fmt yuv420p -movflags +faststart " +
            $"-y \"{outputFilePath}\"";

        return new VideoEditorExportRequest(arguments, outputFilePath, TimeSpan.FromSeconds(duration), false, [.. graph.TempFiles, graphFile]);
    }
}
