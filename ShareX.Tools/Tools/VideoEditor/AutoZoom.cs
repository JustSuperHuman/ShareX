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
using System.Globalization;
using System.Text;

namespace ShareX.Tools;

// Tunables that drive the auto-zoom feel. Amount controls how far the camera pushes in; smoothness
// controls how lazily it glides and holds.
public readonly record struct AutoZoomParameters(bool Enabled, double MaxZoom, double Smoothness)
{
    public static AutoZoomParameters Default { get; } = new(false, 1.8, 0.5);
}

// The camera state (zoom + source-normalized center) at a single instant.
public readonly record struct AutoZoomState(double Zoom, double CenterX, double CenterY)
{
    public static AutoZoomState Neutral { get; } = new(1, 0.5, 0.5);
}

public sealed record AutoZoomKeyframe(double Time, double Zoom, double CenterX, double CenterY);

// A precomputed, densely sampled camera path. Sample() feeds the live preview; GetKeyframes()
// produces a decimated path for the FFmpeg export expression.
public sealed class AutoZoomPlan
{
    private readonly double[] _zoom;
    private readonly double[] _cx;
    private readonly double[] _cy;
    private readonly double _fps;

    public double Duration { get; }
    public double MaxZoom { get; }
    public bool HasZoom { get; }
    public IReadOnlyList<(double Start, double End)> ZoomRegions { get; }

    public static AutoZoomPlan Empty { get; } = new();

    private AutoZoomPlan()
    {
        _zoom = [1, 1];
        _cx = [0.5, 0.5];
        _cy = [0.5, 0.5];
        _fps = 1;
        Duration = 0;
        MaxZoom = 1;
        HasZoom = false;
        ZoomRegions = [];
    }

    internal AutoZoomPlan(double[] zoom, double[] cx, double[] cy, double fps)
    {
        _zoom = zoom;
        _cx = cx;
        _cy = cy;
        _fps = fps;
        Duration = (zoom.Length - 1) / fps;

        double max = 1;
        List<(double, double)> regions = [];
        int regionStart = -1;
        for (int i = 0; i < zoom.Length; i++)
        {
            if (zoom[i] > max) max = zoom[i];
            bool zoomed = zoom[i] > 1.04;
            if (zoomed && regionStart < 0) regionStart = i;
            else if (!zoomed && regionStart >= 0)
            {
                regions.Add((regionStart / fps, (i - 1) / fps));
                regionStart = -1;
            }
        }
        if (regionStart >= 0) regions.Add((regionStart / fps, (zoom.Length - 1) / fps));

        MaxZoom = max;
        HasZoom = max > 1.01;
        ZoomRegions = regions;
    }

    public AutoZoomState Sample(double time)
    {
        if (_zoom.Length < 2) return AutoZoomState.Neutral;

        double pos = System.Math.Clamp(time, 0, Duration) * _fps;
        int i0 = (int)pos;
        if (i0 >= _zoom.Length - 1) return new AutoZoomState(_zoom[^1], _cx[^1], _cy[^1]);
        double f = pos - i0;
        return new AutoZoomState(
            Lerp(_zoom[i0], _zoom[i0 + 1], f),
            Lerp(_cx[i0], _cx[i0 + 1], f),
            Lerp(_cy[i0], _cy[i0 + 1], f));
    }

    // Douglas-Peucker decimation of the (time; zoom, centerX, centerY) polyline. Center error is
    // weighted by how zoomed-in we are, so panning keyframes are only spent where they're visible.
    public IReadOnlyList<AutoZoomKeyframe> GetKeyframes(int maxKeyframes = 260)
    {
        int n = _zoom.Length;
        if (n < 2) return [new AutoZoomKeyframe(0, 1, 0.5, 0.5)];

        double zoomTol = 0.015;
        double centerTol = 0.0035;

        for (int attempt = 0; attempt < 6; attempt++)
        {
            bool[] keep = new bool[n];
            keep[0] = keep[n - 1] = true;
            Simplify(0, n - 1, zoomTol, centerTol, keep);

            List<AutoZoomKeyframe> result = [];
            for (int i = 0; i < n; i++)
            {
                if (keep[i]) result.Add(new AutoZoomKeyframe(i / _fps, _zoom[i], _cx[i], _cy[i]));
            }

            if (result.Count <= maxKeyframes) return result;
            zoomTol *= 1.6;
            centerTol *= 1.6;
        }

        // Fallback: uniform decimation.
        int step = System.Math.Max(1, n / maxKeyframes);
        List<AutoZoomKeyframe> uniform = [];
        for (int i = 0; i < n; i += step) uniform.Add(new AutoZoomKeyframe(i / _fps, _zoom[i], _cx[i], _cy[i]));
        uniform.Add(new AutoZoomKeyframe((n - 1) / _fps, _zoom[n - 1], _cx[n - 1], _cy[n - 1]));
        return uniform;
    }

    private void Simplify(int lo, int hi, double zoomTol, double centerTol, bool[] keep)
    {
        if (hi <= lo + 1) return;

        double maxError = 0;
        int maxIndex = -1;
        for (int i = lo + 1; i < hi; i++)
        {
            double f = (double)(i - lo) / (hi - lo);
            double zoomError = System.Math.Abs(_zoom[i] - Lerp(_zoom[lo], _zoom[hi], f)) / zoomTol;
            double weight = System.Math.Clamp((_zoom[i] - 1) / System.Math.Max(0.0001, MaxZoom - 1), 0, 1);
            double cxError = System.Math.Abs(_cx[i] - Lerp(_cx[lo], _cx[hi], f)) / centerTol * weight;
            double cyError = System.Math.Abs(_cy[i] - Lerp(_cy[lo], _cy[hi], f)) / centerTol * weight;
            double error = System.Math.Max(zoomError, System.Math.Max(cxError, cyError));
            if (error > maxError)
            {
                maxError = error;
                maxIndex = i;
            }
        }

        if (maxError > 1 && maxIndex > 0)
        {
            keep[maxIndex] = true;
            Simplify(lo, maxIndex, zoomTol, centerTol, keep);
            Simplify(maxIndex, hi, zoomTol, centerTol, keep);
        }
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
}

// Turns a recorded cursor track into an elegant camera path: it pushes in around clicks (with a little
// anticipation, since the whole take is known) and where the cursor lingers after moving, then eases
// back out during fast sweeps and idle time.
public static class AutoZoomPlanner
{
    public static AutoZoomPlan Build(ScreenRecordingMotionData data, AutoZoomParameters parameters, double fps = 30)
    {
        if (!parameters.Enabled || data == null || !data.HasSamples) return AutoZoomPlan.Empty;

        double duration = data.Duration;
        if (duration <= 0) duration = data.Samples[^1].T;
        if (duration <= 0) return AutoZoomPlan.Empty;

        int n = System.Math.Max(2, (int)System.Math.Ceiling(duration * fps) + 1);
        double dt = 1.0 / fps;
        double smooth = System.Math.Clamp(parameters.Smoothness, 0, 1);

        // 1. Resample the cursor track onto a uniform grid.
        double[] rx = new double[n];
        double[] ry = new double[n];
        Resample(data.Samples, fps, n, rx, ry);

        // 2. Zero-phase smoothing for a calm, lag-free camera path.
        double tauPos = Lerp(0.12, 0.55, smooth);
        double[] sx = SmoothZeroPhase(rx, dt, tauPos);
        double[] sy = SmoothZeroPhase(ry, dt, tauPos);

        // 3. Cursor speed (normalized units / second), lightly smoothed.
        double[] speed = new double[n];
        for (int i = 1; i < n; i++)
        {
            speed[i] = System.Math.Sqrt(Sq(rx[i] - rx[i - 1]) + Sq(ry[i] - ry[i - 1])) * fps;
        }
        speed[0] = speed.Length > 1 ? speed[1] : 0;
        speed = SmoothZeroPhase(speed, dt, 0.18);

        // 4. Activity envelope: clicks (anticipated via a symmetric bump) + movement-gated dwell.
        double clickSigma = Lerp(0.9, 1.5, smooth);
        double[] env = new double[n];
        double[] recentMotion = ComputeRecentMotion(speed, fps);

        for (int i = 0; i < n; i++)
        {
            double t = i * dt;

            double clickEnv = 0;
            foreach (ScreenRecordingMotionClick click in data.Clicks)
            {
                double d = t - click.T;
                double g = System.Math.Exp(-(d * d) / (2 * clickSigma * clickSigma));
                if (g > clickEnv) clickEnv = g;
            }

            // Dwell: user has slowed down after having moved recently -> intentional focus.
            double dwell = 1 - SmoothStep(0.03, 0.32, speed[i]);
            double dwellEnv = 0.7 * dwell * recentMotion[i];

            env[i] = System.Math.Max(clickEnv, dwellEnv);
        }

        // 5. Asymmetric easing: push in briskly, ease out gracefully so zoom holds through a task.
        double tauIn = Lerp(0.20, 0.42, smooth);
        double tauOut = Lerp(0.55, 1.15, smooth);
        double alphaIn = 1 - System.Math.Exp(-dt / tauIn);
        double alphaOut = 1 - System.Math.Exp(-dt / tauOut);
        double[] envSmoothed = new double[n];
        envSmoothed[0] = env[0];
        for (int i = 1; i < n; i++)
        {
            double target = env[i];
            double alpha = target > envSmoothed[i - 1] ? alphaIn : alphaOut;
            envSmoothed[i] = envSmoothed[i - 1] + alpha * (target - envSmoothed[i - 1]);
        }

        // 6. Build zoom + center. Soft-threshold removes lingering micro-zoom; center blends toward the
        // frame middle when barely zoomed to avoid off-center framing at low zoom.
        double zoomSpan = System.Math.Max(1.0, parameters.MaxZoom) - 1.0;
        double[] zoom = new double[n];
        double[] cx = new double[n];
        double[] cy = new double[n];
        for (int i = 0; i < n; i++)
        {
            double e = System.Math.Clamp((envSmoothed[i] - 0.05) / 0.95, 0, 1);
            zoom[i] = 1 + zoomSpan * e;
            double centerBlend = SmoothStep(0, 0.3, e);
            cx[i] = Lerp(0.5, sx[i], centerBlend);
            cy[i] = Lerp(0.5, sy[i], centerBlend);
        }

        return new AutoZoomPlan(zoom, cx, cy, fps);
    }

    private static void Resample(IReadOnlyList<ScreenRecordingMotionSample> samples, double fps, int n, double[] rx, double[] ry)
    {
        int j = 0;
        for (int i = 0; i < n; i++)
        {
            double t = i / fps;
            while (j < samples.Count - 1 && samples[j + 1].T <= t) j++;

            if (j >= samples.Count - 1)
            {
                rx[i] = samples[^1].X;
                ry[i] = samples[^1].Y;
            }
            else
            {
                ScreenRecordingMotionSample a = samples[j];
                ScreenRecordingMotionSample b = samples[j + 1];
                double span = b.T - a.T;
                double f = span > 1e-6 ? System.Math.Clamp((t - a.T) / span, 0, 1) : 0;
                rx[i] = Lerp(a.X, b.X, f);
                ry[i] = Lerp(a.Y, b.Y, f);
            }
        }
    }

    private static double[] SmoothZeroPhase(double[] input, double dt, double tau)
    {
        int n = input.Length;
        double[] output = new double[n];
        if (n == 0) return output;

        double alpha = 1 - System.Math.Exp(-dt / System.Math.Max(1e-4, tau));

        output[0] = input[0];
        for (int i = 1; i < n; i++) output[i] = output[i - 1] + alpha * (input[i] - output[i - 1]);
        for (int i = n - 2; i >= 0; i--) output[i] = output[i + 1] + alpha * (output[i] - output[i + 1]);

        return output;
    }

    // 1 where the cursor moved meaningfully within a short window (so dwell zoom only triggers after
    // intentional movement, never on a completely idle screen).
    private static double[] ComputeRecentMotion(double[] speed, double fps)
    {
        int n = speed.Length;
        int window = System.Math.Max(1, (int)(1.5 * fps));
        double[] result = new double[n];
        for (int i = 0; i < n; i++)
        {
            double peak = 0;
            for (int k = System.Math.Max(0, i - window); k <= System.Math.Min(n - 1, i + window); k++)
            {
                if (speed[k] > peak) peak = speed[k];
            }
            result[i] = SmoothStep(0.05, 0.25, peak);
        }
        return result;
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
    private static double Sq(double v) => v * v;

    private static double SmoothStep(double edge0, double edge1, double x)
    {
        if (edge1 <= edge0) return x >= edge1 ? 1 : 0;
        double t = System.Math.Clamp((x - edge0) / (edge1 - edge0), 0, 1);
        return t * t * (3 - 2 * t);
    }
}

// Builds the FFmpeg zoompan expressions and computes the on-screen camera rectangle for the preview.
public static class AutoZoomFilter
{
    // The window (in source pixels, inside the manual crop) that the output camera shows at this instant.
    public static Rect ComputeCameraRect(int sourceWidth, int sourceHeight, Rect crop, AutoZoomState state)
    {
        double zoom = System.Math.Max(1, state.Zoom);
        double winW = crop.Width / zoom;
        double winH = crop.Height / zoom;

        double centerX = System.Math.Clamp(state.CenterX * sourceWidth, crop.X + winW / 2, crop.X + crop.Width - winW / 2);
        double centerY = System.Math.Clamp(state.CenterY * sourceHeight, crop.Y + winH / 2, crop.Y + crop.Height - winH / 2);

        return new Rect(centerX - winW / 2, centerY - winH / 2, winW, winH);
    }

    // Builds "[in]zoompan=...[out]" for the given decimated keyframes. Center keyframes are expressed
    // in the cropped frame's normalized space; x/y use zoompan's built-in "zoom" variable to stay compact.
    public static string BuildZoomPanFilter(
        IReadOnlyList<AutoZoomKeyframe> keyframes,
        int sourceWidth,
        int sourceHeight,
        Rect crop,
        int outputWidth,
        int outputHeight,
        double fps,
        string inputLabel,
        string outputLabel)
    {
        int count = keyframes.Count;
        double[] times = new double[count];
        double[] zoom = new double[count];
        double[] ccx = new double[count];
        double[] ccy = new double[count];

        for (int i = 0; i < count; i++)
        {
            AutoZoomKeyframe kf = keyframes[i];
            times[i] = kf.Time;
            zoom[i] = kf.Zoom;
            ccx[i] = (kf.CenterX * sourceWidth - crop.X) / crop.Width;
            ccy[i] = (kf.CenterY * sourceHeight - crop.Y) / crop.Height;
        }

        string time = $"(on/{Num(fps)})";
        string zoomExpr = BuildClipSum(times, zoom, time);
        string cxExpr = BuildClipSum(times, ccx, time);
        string cyExpr = BuildClipSum(times, ccy, time);

        string x = $"clip(({cxExpr})*iw*zoom-iw/2,0,iw*zoom-iw)";
        string y = $"clip(({cyExpr})*ih*zoom-ih/2,0,ih*zoom-ih)";

        return $"{inputLabel}zoompan=z='{zoomExpr}':x='{x}':y='{y}':d=1:fps={Num(fps)}:s={outputWidth}x{outputHeight}{outputLabel}";
    }

    // Piecewise-linear interpolation as a sum of clamped ramps:
    //   v0 + (v1-v0)*clip((t-t0)/(t1-t0),0,1) + (v2-v1)*clip((t-t1)/(t2-t1),0,1) + ...
    private static string BuildClipSum(double[] times, double[] values, string timeExpr)
    {
        StringBuilder sb = new();
        sb.Append(Num(values[0]));

        for (int i = 0; i < values.Length - 1; i++)
        {
            double dv = values[i + 1] - values[i];
            double span = times[i + 1] - times[i];
            if (System.Math.Abs(dv) < 1e-6 || span <= 1e-6) continue;

            sb.Append(dv >= 0 ? "+" : "-");
            sb.Append(Num(System.Math.Abs(dv)));
            sb.Append("*clip((");
            sb.Append(timeExpr);
            sb.Append('-');
            sb.Append(Num(times[i]));
            sb.Append(")/");
            sb.Append(Num(span));
            sb.Append(",0,1)");
        }

        return sb.ToString();
    }

    private static string Num(double value) => value.ToString("0.#####", CultureInfo.InvariantCulture);
}
