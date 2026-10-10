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
using ShareX.HelpersLib;
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace ShareX.Tools;

// Collects the pieces of an ffmpeg -filter_complex export: filter statements, extra image inputs
// (sprites rendered on the fly), and the temp files that must be deleted after the run. Stages chain
// through generated labels so effects can be composed in any combination.
public sealed class ExportGraphBuilder
{
    private int _labelCounter;
    private int _nextInputIndex = 1; // input 0 is the recording itself

    public List<string> Parts { get; } = [];
    public List<string> ExtraInputs { get; } = [];
    public List<string> TempFiles { get; } = [];

    public bool HasFilters => Parts.Count > 0;

    public string NewLabel() => $"[fx{_labelCounter++}]";

    // Registers a still image input and returns its input index. Deliberately NOT "-loop 1": overlay's
    // default eof_action=repeat holds the single frame forever, and the finite input lets the graph
    // terminate with the video instead of running away generating frames.
    public int AddImageInput(string path)
    {
        ExtraInputs.Add($"-i \"{path}\"");
        TempFiles.Add(path);
        return _nextInputIndex++;
    }

    // Appends "<in><filter><newLabel>" and returns the new label.
    public string Chain(string inputLabel, string filter)
    {
        string outputLabel = NewLabel();
        Parts.Add($"{inputLabel}{filter}{outputLabel}");
        return outputLabel;
    }

    public string BuildGraph(string finalVideoLabel)
    {
        // The export always maps "[v]"; forward the last label through a passthrough filter.
        List<string> parts = [.. Parts, $"{finalVideoLabel}null[v]"];
        return string.Join(";", parts);
    }
}

// A single recorded click with coordinates normalized (0..1) inside the source frame. Time is on
// whichever clock the consumer needs: absolute recording time for the live preview, trimmed-clip time
// for the export graph.
public sealed record VideoClickHighlight(double Time, double X, double Y);

// Renders the sprite bitmaps the effects overlay onto the video. Sprites are plain PNGs written to
// temp files so ffmpeg can consume them as looping inputs. Encoding is done with a tiny built-in PNG
// writer so sprites can be produced on any thread without touching the UI platform.
public static class VideoEffectSprites
{
    public static string WriteRipplePng(int canvasSize, double radiusFraction, double ringWidthFraction, double alpha,
        byte r, byte g, byte b, bool phoneTap = false, bool tapDot = false, double tapImpact = 0, double tapBrightness = 1)
    {
        int size = Math.Max(8, canvasSize);
        byte[] pixels = new byte[size * size * 4];
        double center = (size - 1) / 2.0;
        double radius = radiusFraction * size;
        double sigma = Math.Max(1.5, ringWidthFraction * size);

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double distance = Math.Sqrt((x - center) * (x - center) + (y - center) * (y - center));
                double d = distance - radius;
                double ring = Math.Exp(-(d * d) / (2 * sigma * sigma));
                double fill = distance < radius ? (phoneTap ? 0.03 : 0.25) : 0;
                double dot = tapDot && distance < size * 0.075 ? 0.9 : 0;
                double pulse = Math.Max(Math.Max(ring, fill), dot);
                if (phoneTap)
                {
                    double glow = Math.Exp(-(d * d) / (5.78 * sigma * sigma)) * (0.06 + tapImpact * 0.12);
                    double echoDistance = distance - radius * 0.62;
                    double echo = Math.Exp(-(echoDistance * echoDistance) / (2 * sigma * sigma)) * tapImpact * 0.50;
                    pulse = Math.Max(Math.Max(ring + glow, echo), Math.Max(fill, dot));
                }

                double a = Math.Clamp(alpha * pulse * (phoneTap ? tapBrightness : 1), 0, 1);

                SetPixel(pixels, size, x, y, r, g, b, (byte)(a * 255));
            }
        }

        return WritePng(pixels, size, size);
    }

    // A soft white disc used as the moving hole of the spotlight mask: opaque in the middle, easing to
    // transparent at the rim.
    public static string WriteSpotlightHolePng(int holeRadius)
    {
        int radius = Math.Max(8, holeRadius);
        int size = (int)Math.Ceiling(radius * 3.0) / 2 * 2;
        byte[] pixels = new byte[size * size * 4];
        double center = (size - 1) / 2.0;
        double inner = radius * 0.75;
        double outer = radius * 1.25;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double distance = Math.Sqrt((x - center) * (x - center) + (y - center) * (y - center));
                double t = Math.Clamp((distance - inner) / (outer - inner), 0, 1);
                double a = 1 - t * t * (3 - 2 * t); // smoothstep falloff
                SetPixel(pixels, size, x, y, 255, 255, 255, (byte)(a * 255));
            }
        }

        return WritePng(pixels, size, size);
    }

    // The studio backdrop: a diagonal gradient with a soft shadow already baked in where the inset
    // video will sit, so the export needs no blur filters.
    public static string WriteStudioBackgroundPng(
        int width, int height, int videoWidth, int videoHeight,
        (byte R, byte G, byte B) topColor, (byte R, byte G, byte B) bottomColor)
    {
        byte[] pixels = new byte[width * height * 4];
        double videoLeft = (width - videoWidth) / 2.0;
        double videoTop = (height - videoHeight) / 2.0;
        double shadowOffset = videoHeight * 0.025;
        double sigma = Math.Max(4, videoHeight * 0.035);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double t = (x / (double)Math.Max(1, width - 1) + y / (double)Math.Max(1, height - 1)) / 2;
                double r = topColor.R + (bottomColor.R - topColor.R) * t;
                double g = topColor.G + (bottomColor.G - topColor.G) * t;
                double b = topColor.B + (bottomColor.B - topColor.B) * t;

                // Distance outside the (shadow-shifted) video rectangle drives a soft drop shadow.
                double dx = Math.Max(0, Math.Max(videoLeft - x, x - (videoLeft + videoWidth)));
                double dy = Math.Max(0, Math.Max(videoTop + shadowOffset - y, y - (videoTop + shadowOffset + videoHeight)));
                double distance = Math.Sqrt(dx * dx + dy * dy);
                double shadow = 0.55 * Math.Exp(-(distance * distance) / (2 * sigma * sigma));
                r *= 1 - shadow;
                g *= 1 - shadow;
                b *= 1 - shadow;

                SetPixel(pixels, width, x, y, (byte)r, (byte)g, (byte)b, 255);
            }
        }

        return WritePng(pixels, width, height);
    }

    // Anti-aliased white rounded rectangle used as the inset video's alpha mask.
    public static string WriteRoundedMaskPng(int width, int height, double cornerRadius)
    {
        byte[] pixels = new byte[width * height * 4];
        double radius = Math.Clamp(cornerRadius, 0, Math.Min(width, height) / 2.0);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                // Signed distance to the rounded rectangle: positive outside.
                double cx = Math.Clamp(x + 0.5, radius, width - radius);
                double cy = Math.Clamp(y + 0.5, radius, height - radius);
                double dx = x + 0.5 - cx;
                double dy = y + 0.5 - cy;
                double distance = Math.Sqrt(dx * dx + dy * dy) - radius;
                double a = Math.Clamp(0.5 - distance, 0, 1);
                byte value = (byte)(a * 255);
                SetPixel(pixels, width, x, y, value, value, value, 255);
            }
        }

        return WritePng(pixels, width, height);
    }

    // A left-to-right accent gradient strip; the export slides it in from the left so the visible part
    // acts as a progress bar.
    public static string WriteProgressBarPng(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        (byte r1, byte g1, byte b1) = ((byte)62, (byte)131, (byte)242);
        (byte r2, byte g2, byte b2) = ((byte)111, (byte)76, (byte)255);

        for (int x = 0; x < width; x++)
        {
            double t = x / (double)Math.Max(1, width - 1);
            byte r = (byte)(r1 + (r2 - r1) * t);
            byte g = (byte)(g1 + (g2 - g1) * t);
            byte b = (byte)(b1 + (b2 - b1) * t);
            for (int y = 0; y < height; y++)
            {
                SetPixel(pixels, width, x, y, r, g, b, 230);
            }
        }

        return WritePng(pixels, width, height);
    }

    internal static void SetPixel(byte[] rgbaPixels, int width, int x, int y, byte r, byte g, byte b, byte a)
    {
        int i = (y * width + x) * 4;
        rgbaPixels[i] = r;
        rgbaPixels[i + 1] = g;
        rgbaPixels[i + 2] = b;
        rgbaPixels[i + 3] = a;
    }

    // Minimal PNG writer: 8-bit RGBA, no interlace, filter 0 scanlines, zlib-deflated IDAT.
    internal static string WritePng(byte[] rgbaPixels, int width, int height)
    {
        byte[] raw = new byte[height * (1 + width * 4)];
        for (int y = 0; y < height; y++)
        {
            int rowStart = y * (1 + width * 4);
            raw[rowStart] = 0; // filter: none
            Buffer.BlockCopy(rgbaPixels, y * width * 4, raw, rowStart + 1, width * 4);
        }

        using MemoryStream compressed = new();
        using (ZLibStream zlib = new(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(raw, 0, raw.Length);
        }

        using MemoryStream png = new();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        byte[] header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;  // bit depth
        header[9] = 6;  // color type: RGBA
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);

        string path = Path.Combine(Path.GetTempPath(), $"ShareX-videoedit-fx-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, png.ToArray());
        return path;
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);

        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);

        uint crc = Crc32(typeBytes, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        uint[] table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(byte[] first, byte[] second)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte value in first) crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        foreach (byte value in second) crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }
}

// Builds the ffmpeg filter statements for each effect. Effects that live in source coordinates
// (ripples) run before auto-zoom and speed so they travel with the content.
public static class PhoneTapVisuals
{
    public const int PhaseCount = 4;

    public static double Duration(double impact) => 0.42 + 0.38 * Math.Clamp(impact, 0, 1);
    public static double Radius(double progress, double impact) =>
        0.20 + (0.24 + 0.02 * Math.Clamp(impact, 0, 1)) * Math.Clamp(progress, 0, 1);
    public static double Alpha(double progress) => 0.85 - 0.60 * Math.Clamp(progress, 0, 1);
    public static double RingWidth(double impact) => 0.025 + 0.005 * Math.Clamp(impact, 0, 1);
}

public static class VideoEffectsGraph
{
    public const int MaxRippleClicks = 60;
    private const double RipplePhaseSeconds = 0.14;
    private static readonly (double Radius, double Alpha)[] RipplePhases =
    [
        (0.20, 0.85),
        (0.32, 0.55),
        (0.44, 0.30)
    ];

    // Shifts raw clicks onto the trimmed clip's clock, dropping the ones outside the selection.
    // Coordinates stay normalized; AddClickRipples resolves them against the crop.
    public static List<VideoClickHighlight> ResolveClicks(
        IReadOnlyList<ScreenRecordingMotionClick> clicks,
        double inPoint,
        double selectionLength)
    {
        List<VideoClickHighlight> resolved = [];
        foreach (ScreenRecordingMotionClick click in clicks)
        {
            double time = click.T - inPoint;
            if (time < -0.05 || time > selectionLength) continue;
            resolved.Add(new VideoClickHighlight(Math.Max(0, time), click.X, click.Y));
        }
        return resolved;
    }

    public static int GetRippleCanvasSize(Rect crop)
    {
        int size = (int)(Math.Min(crop.Width, crop.Height) * 0.13);
        return Math.Max(28, size / 2 * 2);
    }

    // Overlays a three-phase expanding pulse at every click. One sprite per phase is rendered once and
    // split per click, so the graph stays compact: clicks × 3 overlays.
    public static string AddClickRipples(
        ExportGraphBuilder graph,
        string inputLabel,
        IReadOnlyList<VideoClickHighlight> clicks,
        int sourceWidth,
        int sourceHeight,
        Rect crop,
        bool phoneTap = false,
        double tapSize = 1.5,
        double tapBrightness = 1.3,
        double tapImpact = 0.4)
    {
        // Resolve to cropped-frame pixels and drop clicks the crop cuts away.
        List<VideoClickHighlight> limited = [];
        foreach (VideoClickHighlight click in clicks)
        {
            double x = click.X * sourceWidth - crop.X;
            double y = click.Y * sourceHeight - crop.Y;
            if (x < 0 || y < 0 || x > crop.Width || y > crop.Height) continue;
            limited.Add(click with { X = x, Y = y });
            if (!phoneTap && limited.Count >= MaxRippleClicks) break;
        }
        if (limited.Count == 0) return inputLabel;

        int canvas = GetRippleCanvasSize(crop);
        if (phoneTap) canvas = Math.Max(14, (int)Math.Round(canvas * Math.Clamp(tapSize, 0.5, 3) / 2) * 2);

        string current = inputLabel;
        int phaseCount = phoneTap ? PhoneTapVisuals.PhaseCount : RipplePhases.Length;
        double phaseSeconds = phoneTap ? PhoneTapVisuals.Duration(tapImpact) / phaseCount : RipplePhaseSeconds;
        for (int phase = 0; phase < phaseCount; phase++)
        {
            double progress = phase / (double)(phaseCount - 1);
            (double radius, double alpha) = phoneTap
                ? (PhoneTapVisuals.Radius(progress, tapImpact), PhoneTapVisuals.Alpha(progress))
                : RipplePhases[phase];
            string sprite = VideoEffectSprites.WriteRipplePng(canvas, radius,
                phoneTap ? PhoneTapVisuals.RingWidth(tapImpact) : 0.055, alpha,
                255, phoneTap ? (byte)255 : (byte)202, phoneTap ? (byte)255 : (byte)87,
                phoneTap, phoneTap && phase == 0, tapImpact, tapBrightness);
            int inputIndex = graph.AddImageInput(sprite);

            string spriteLabel = $"[{inputIndex}:v]";
            if (limited.Count > 1)
            {
                StringBuilder split = new($"[{inputIndex}:v]split={limited.Count}");
                for (int c = 0; c < limited.Count; c++) split.Append($"[rip{phase}_{c}]");
                graph.Parts.Add(split.ToString());
            }

            for (int c = 0; c < limited.Count; c++)
            {
                VideoClickHighlight click = limited[c];
                string overlaySource = limited.Count > 1 ? $"[rip{phase}_{c}]" : spriteLabel;
                double t0 = click.Time + phase * phaseSeconds;
                double t1 = t0 + phaseSeconds;
                int x = (int)Math.Round(click.X - canvas / 2.0);
                int y = (int)Math.Round(click.Y - canvas / 2.0);
                string next = graph.NewLabel();
                graph.Parts.Add($"{current}{overlaySource}overlay=x={x}:y={y}:enable='between(t,{Num(t0)},{Num(t1)})'{next}");
                current = next;
            }
        }

        return current;
    }

    // How dark the area outside the spotlight goes (multiplier on each color channel).
    public const double SpotlightDimFactor = 0.45;

    // Fraction of the canvas the inset video occupies in the studio layout.
    public const double StudioInset = 0.88;

    // Overlays a thin progress bar along the bottom edge, sliding in from the left over the output's
    // duration. Runs last, on the output clock, so speed changes are reflected exactly.
    public static string AddProgressBar(ExportGraphBuilder graph, string inputLabel, int frameWidth, int frameHeight, double outputDuration)
    {
        if (outputDuration <= 0) return inputLabel;

        int barHeight = Math.Max(4, frameHeight / 72);
        int barIndex = graph.AddImageInput(VideoEffectSprites.WriteProgressBarPng(frameWidth, barHeight));

        string outLabel = graph.NewLabel();
        graph.Parts.Add($"{inputLabel}[{barIndex}:v]overlay=x='-w+w*t/{Num(outputDuration)}':y=H-h{outLabel}");
        return outLabel;
    }

    // Frames the video on a gradient canvas with rounded corners and a soft shadow — the polished
    // "studio" look for sharing. Runs on the output clock, after speed and scaling.
    public static string AddStudioBackground(ExportGraphBuilder graph, string inputLabel, int frameWidth, int frameHeight, (byte R, byte G, byte B) topColor, (byte R, byte G, byte B) bottomColor)
    {
        int videoHeight = (int)(frameHeight * StudioInset) / 2 * 2;
        int videoWidth = (int)Math.Round(videoHeight * (frameWidth / (double)frameHeight)) / 2 * 2;
        double cornerRadius = Math.Max(8, Math.Min(videoWidth, videoHeight) * 0.03);

        int backgroundIndex = graph.AddImageInput(VideoEffectSprites.WriteStudioBackgroundPng(frameWidth, frameHeight, videoWidth, videoHeight, topColor, bottomColor));
        int maskIndex = graph.AddImageInput(VideoEffectSprites.WriteRoundedMaskPng(videoWidth, videoHeight, cornerRadius));

        string scaled = graph.Chain(inputLabel, $"scale={videoWidth}:{videoHeight}");
        string cut = graph.NewLabel();
        graph.Parts.Add($"{scaled}[{maskIndex}:v]alphamerge{cut}");

        string outLabel = graph.NewLabel();
        int x = (frameWidth - videoWidth) / 2;
        int y = (frameHeight - videoHeight) / 2;
        graph.Parts.Add($"[{backgroundIndex}:v]{cut}overlay=x={x}:y={y}{outLabel}");
        return outLabel;
    }

    // Dims everything except a soft circle that follows the recorded cursor. Built from native filters:
    // the frame is split into a dimmed base and a bright top, the top's alpha comes from a black canvas
    // with the moving hole sprite overlaid, and the two are recombined.
    public static string AddSpotlight(
        ExportGraphBuilder graph,
        string inputLabel,
        IReadOnlyList<(double T, double X, double Y)> cursorPath,
        int sourceWidth,
        int sourceHeight,
        Rect crop,
        double radiusFraction,
        double selectionLength,
        double fps)
    {
        if (cursorPath.Count < 2) return inputLabel;

        int holeRadius = (int)(Math.Min(crop.Width, crop.Height) * Math.Clamp(radiusFraction, 0.05, 0.5));
        string sprite = VideoEffectSprites.WriteSpotlightHolePng(holeRadius);
        int spriteIndex = graph.AddImageInput(sprite);
        int canvas = (int)Math.Ceiling(holeRadius * 3.0) / 2 * 2;

        // Cursor keyframes → pixel offsets of the sprite's top-left corner inside the cropped frame.
        double[] times = new double[cursorPath.Count];
        double[] xs = new double[cursorPath.Count];
        double[] ys = new double[cursorPath.Count];
        for (int i = 0; i < cursorPath.Count; i++)
        {
            times[i] = cursorPath[i].T;
            xs[i] = cursorPath[i].X * sourceWidth - crop.X - canvas / 2.0;
            ys[i] = cursorPath[i].Y * sourceHeight - crop.Y - canvas / 2.0;
        }

        string dim = Num(SpotlightDimFactor);
        string baseLabel = graph.NewLabel();
        string topLabel = graph.NewLabel();
        string darkLabel = graph.NewLabel();
        string canvasLabel = graph.NewLabel();
        string maskLabel = graph.NewLabel();
        string cutLabel = graph.NewLabel();

        graph.Parts.Add($"{inputLabel}split=2{baseLabel}{topLabel}");
        graph.Parts.Add($"{baseLabel}colorchannelmixer=rr={dim}:gg={dim}:bb={dim}{darkLabel}");
        // The mask canvas duration must match the clip exactly: framesync repeats the shorter input's
        // last frame, so any LONGER side stream would stretch the whole output past the video's end.
        graph.Parts.Add($"color=c=black:s={(int)crop.Width}x{(int)crop.Height}:r={Num(fps)}:d={Num(selectionLength)}{canvasLabel}");
        graph.Parts.Add($"{canvasLabel}[{spriteIndex}:v]overlay=x='{BuildLinearExpr(times, xs)}':y='{BuildLinearExpr(times, ys)}'{maskLabel}");
        graph.Parts.Add($"{topLabel}{maskLabel}alphamerge{cutLabel}");

        string outLabel = graph.NewLabel();
        graph.Parts.Add($"{darkLabel}{cutLabel}overlay=0:0{outLabel}");
        return outLabel;
    }

    // Thins the recorded cursor track to a compact set of keyframes on the trimmed clip's clock,
    // accurate to about <tolerance> in normalized coordinates.
    public static List<(double T, double X, double Y)> DecimateCursorPath(
        IReadOnlyList<ScreenRecordingMotionSample> samples,
        double inPoint,
        double selectionLength,
        double tolerance = 0.004,
        int maxPoints = 160)
    {
        if (samples.Count < 2 || selectionLength <= 0) return [];

        // Resample onto a uniform grid first so irregular capture timing can't skew the simplification.
        double fps = 12;
        int n = Math.Clamp((int)(selectionLength * fps) + 1, 2, 2400);
        double step = selectionLength / (n - 1);
        double[] t = new double[n];
        double[] x = new double[n];
        double[] y = new double[n];
        for (int i = 0; i < n; i++)
        {
            t[i] = i * step;
            (x[i], y[i]) = SampleCursor(samples, inPoint + t[i]);
        }

        for (int attempt = 0; attempt < 6; attempt++)
        {
            bool[] keep = new bool[n];
            keep[0] = keep[n - 1] = true;
            SimplifyPath(t, x, y, 0, n - 1, tolerance, keep);

            List<(double, double, double)> result = [];
            for (int i = 0; i < n; i++)
            {
                if (keep[i]) result.Add((t[i], x[i], y[i]));
            }
            if (result.Count <= maxPoints) return result;
            tolerance *= 1.6;
        }

        List<(double, double, double)> uniform = [];
        int stride = Math.Max(1, n / maxPoints);
        for (int i = 0; i < n; i += stride) uniform.Add((t[i], x[i], y[i]));
        if (uniform[^1].Item1 < t[n - 1]) uniform.Add((t[n - 1], x[n - 1], y[n - 1]));
        return uniform;
    }

    // Linear interpolation of the cursor position at an absolute recording time.
    public static (double X, double Y) SampleCursor(IReadOnlyList<ScreenRecordingMotionSample> samples, double time)
    {
        if (samples.Count == 0) return (0.5, 0.5);
        if (time <= samples[0].T) return (samples[0].X, samples[0].Y);
        if (time >= samples[^1].T) return (samples[^1].X, samples[^1].Y);

        int lo = 0, hi = samples.Count - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (samples[mid].T <= time) lo = mid;
            else hi = mid;
        }

        ScreenRecordingMotionSample a = samples[lo];
        ScreenRecordingMotionSample b = samples[hi];
        double span = b.T - a.T;
        double f = span > 1e-6 ? (time - a.T) / span : 0;
        return (a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f);
    }

    private static void SimplifyPath(double[] t, double[] x, double[] y, int lo, int hi, double tolerance, bool[] keep)
    {
        if (hi <= lo + 1) return;

        double maxError = 0;
        int maxIndex = -1;
        double span = Math.Max(1e-9, t[hi] - t[lo]);
        for (int i = lo + 1; i < hi; i++)
        {
            double f = (t[i] - t[lo]) / span;
            double ex = x[i] - (x[lo] + (x[hi] - x[lo]) * f);
            double ey = y[i] - (y[lo] + (y[hi] - y[lo]) * f);
            double error = Math.Sqrt(ex * ex + ey * ey);
            if (error > maxError)
            {
                maxError = error;
                maxIndex = i;
            }
        }

        if (maxError > tolerance && maxIndex > 0)
        {
            keep[maxIndex] = true;
            SimplifyPath(t, x, y, lo, maxIndex, tolerance, keep);
            SimplifyPath(t, x, y, maxIndex, hi, tolerance, keep);
        }
    }

    // Piecewise-linear value of t as a sum of clamped ramps — the same technique the auto-zoom
    // expressions use, here against overlay's per-frame "t" variable.
    public static string BuildLinearExpr(double[] times, double[] values)
    {
        StringBuilder sb = new();
        sb.Append(Num(values[0]));
        for (int i = 0; i < values.Length - 1; i++)
        {
            double dv = values[i + 1] - values[i];
            double span = times[i + 1] - times[i];
            // Threshold stays tiny: every skipped delta shifts the rest of the cumulative sum.
            if (Math.Abs(dv) < 1e-3 || span <= 1e-6) continue;

            sb.Append(dv >= 0 ? "+" : "-");
            sb.Append(Num(Math.Abs(dv)));
            sb.Append("*clip((t-");
            sb.Append(Num(times[i]));
            sb.Append(")/");
            sb.Append(Num(span));
            sb.Append(",0,1)");
        }
        return sb.ToString();
    }

    internal static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
