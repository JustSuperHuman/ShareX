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

using ShareX.HelpersLib;

namespace ShareX.Tools;

// Finds the stretches of a recording where nothing was happening — no clicks and no meaningful cursor
// travel — so the editor can fast-forward or cut them in one click. Works on the same motion sidecar
// that drives auto-zoom.
public static class IdleDetector
{
    // Cursor travel (in screen-normalized units) that counts as "the user did something". Measured
    // against the last activity anchor rather than the previous sample, so slow deliberate drift still
    // registers while sensor jitter does not.
    private const double MovementThreshold = 0.008;

    // Breathing room kept around each activity burst so cuts never clip the tail of a motion.
    private const double Padding = 0.4;

    public static IReadOnlyList<(double Start, double End)> FindIdleSpans(
        ScreenRecordingMotionData data,
        double inPoint,
        double outPoint,
        double minIdleSeconds)
    {
        if (data is not { HasSamples: true } || outPoint - inPoint <= minIdleSeconds) return [];

        List<double> activity = [];
        foreach (ScreenRecordingMotionClick click in data.Clicks ?? []) activity.Add(click.T);

        ScreenRecordingMotionSample anchor = data.Samples[0];
        foreach (ScreenRecordingMotionSample sample in data.Samples)
        {
            double dx = sample.X - anchor.X;
            double dy = sample.Y - anchor.Y;
            if (Math.Sqrt(dx * dx + dy * dy) >= MovementThreshold)
            {
                activity.Add(sample.T);
                anchor = sample;
            }
        }

        activity.Sort();

        // Idle spans are the padded gaps between consecutive activity moments, including the stretches
        // before the first and after the last one.
        List<(double Start, double End)> spans = [];
        double gapStart = inPoint;
        foreach (double t in activity)
        {
            if (t > outPoint) break;
            if (t < inPoint) continue;
            AddSpan(spans, gapStart, t - Padding, inPoint, outPoint, minIdleSeconds);
            gapStart = t + Padding;
        }
        AddSpan(spans, gapStart, outPoint, inPoint, outPoint, minIdleSeconds);

        return spans;
    }

    public static double TotalSeconds(IReadOnlyList<(double Start, double End)> spans)
    {
        double total = 0;
        foreach ((double start, double end) in spans) total += end - start;
        return total;
    }

    private static void AddSpan(List<(double, double)> spans, double start, double end, double inPoint, double outPoint, double minIdleSeconds)
    {
        start = Math.Max(start, inPoint);
        end = Math.Min(end, outPoint);
        if (end - start >= minIdleSeconds) spans.Add((start, end));
    }
}
