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

using System.Globalization;
using System.Text;

namespace ShareX.Tools;

// A contiguous slice of the trimmed clip that plays at a fixed speed. Start/End are absolute source
// seconds (the same clock as the timeline); Speed is a multiplier (2 = twice as fast, 0.5 = half).
// Removed segments stay in the lane (so they can be restored) but are cut from playback and export.
public sealed record SpeedSegment(double Start, double End, double Speed, bool Removed = false)
{
    public double SourceDuration => Math.Max(0, End - Start);
    public double OutputDuration => Removed ? 0 : Speed > 1e-6 ? SourceDuration / Speed : SourceDuration;

    public SpeedSegment WithSpeed(double speed) => this with { Speed = Math.Clamp(speed, VideoSpeedGraph.MinSpeed, VideoSpeedGraph.MaxSpeed), Removed = false };
    public SpeedSegment WithRemoved(bool removed) => this with { Removed = removed };
}

// Builds the FFmpeg filter graph that plays each segment at its own speed, and the helpers the editor
// needs to reason about the warped output timeline (export duration, keyframe remapping).
public static class VideoSpeedGraph
{
    public const double MinSpeed = 0.25;
    public const double MaxSpeed = 4.0;

    // Speeds are "close enough to unchanged" within this tolerance, so we can skip the whole variable-speed
    // graph when the user hasn't actually altered anything.
    public static bool IsUnity(double speed) => Math.Abs(speed - 1) < 1e-3;

    // True when the segment lane changes the output at all: a non-1× speed or a removed (cut) clip.
    public static bool HasEdits(IReadOnlyList<SpeedSegment> segments) => segments.Any(s => !IsUnity(s.Speed) || s.Removed);

    public static bool HasRemovals(IReadOnlyList<SpeedSegment> segments) => segments.Any(s => s.Removed);

    // Total length of the sped-up result — used both for the export progress bar and the preview clock.
    public static double OutputDuration(IReadOnlyList<SpeedSegment> segments)
    {
        double total = 0;
        foreach (SpeedSegment segment in segments) total += segment.OutputDuration;
        return total;
    }

    // Output-clock time reached when the source playhead sits at <sourceTime> — maps preview position
    // onto the exported timeline (removed clips contribute nothing, sped clips compress).
    public static double OutputTimeAt(IReadOnlyList<SpeedSegment> segments, double sourceTime)
    {
        double elapsed = 0;
        foreach (SpeedSegment segment in segments)
        {
            if (sourceTime >= segment.End)
            {
                elapsed += segment.OutputDuration;
            }
            else if (sourceTime > segment.Start)
            {
                if (!segment.Removed && segment.Speed > 1e-6) elapsed += (sourceTime - segment.Start) / segment.Speed;
                break;
            }
            else
            {
                break;
            }
        }
        return elapsed;
    }

    // The speed in effect at a source time — drives the WYSIWYG preview clock.
    public static double SpeedAt(IReadOnlyList<SpeedSegment> segments, double sourceTime)
    {
        foreach (SpeedSegment segment in segments)
        {
            if (sourceTime >= segment.Start && sourceTime < segment.End) return segment.Speed;
        }
        return segments.Count > 0 ? segments[^1].Speed : 1;
    }

    // atempo only accepts 0.5–2.0 per instance, so factors outside that range are expressed as a chain.
    public static string AtempoChain(double factor)
    {
        factor = Math.Clamp(factor, MinSpeed, MaxSpeed);
        List<double> stages = [];
        while (factor > 2.0 + 1e-6) { stages.Add(2.0); factor /= 2.0; }
        while (factor < 0.5 - 1e-6) { stages.Add(0.5); factor *= 2.0; }
        stages.Add(factor);
        return string.Join(",", stages.Select(s => $"atempo={Num(s)}"));
    }

    // Builds a filter graph that emits the variable-speed video on <videoOut> and, when the source has
    // audio, the tempo-matched audio on [a]. Segment times are relative to the trimmed clip (0-based,
    // matching an input-seeked stream).
    //
    // <videoBaseLabel> lets the caller feed in an already-prepared video stream (e.g. a cropped +
    // zoompan'd stream). The speed pass MUST run last in the graph: zoompan re-times its output to a
    // constant frame rate by frame count, so running it after setpts would undo the speed change.
    public static string Build(
        IReadOnlyList<SpeedSegment> segments,
        bool cropNeeded,
        int cropX,
        int cropY,
        int cropWidth,
        int cropHeight,
        bool hasAudio,
        string videoOut,
        string? videoBaseLabel = null)
    {
        StringBuilder sb = new();
        // Removed clips simply never enter the graph; the kept clips are trimmed out and re-joined.
        List<SpeedSegment> kept = [.. segments.Where(s => !s.Removed)];
        if (kept.Count == 0) kept = [.. segments];
        int n = kept.Count;

        string videoBase;
        if (videoBaseLabel != null)
        {
            videoBase = videoBaseLabel;
        }
        else if (cropNeeded)
        {
            sb.Append($"[0:v]crop={cropWidth}:{cropHeight}:{cropX}:{cropY}[vbase];");
            videoBase = "[vbase]";
        }
        else
        {
            videoBase = "[0:v]";
        }

        // A single kept clip that spans the whole lane needs no trim; a single kept clip out of many
        // still needs its trim window.
        if (n == 1 && segments.Count == 1)
        {
            sb.Append($"{videoBase}setpts=(PTS-STARTPTS)/{Num(kept[0].Speed)}{videoOut}");
        }
        else if (n == 1)
        {
            sb.Append($"{videoBase}trim=start={Num(kept[0].Start)}:end={Num(kept[0].End)},setpts=(PTS-STARTPTS)/{Num(kept[0].Speed)}{videoOut}");
        }
        else
        {
            sb.Append(videoBase).Append("split=").Append(n);
            for (int i = 0; i < n; i++) sb.Append($"[vsrc{i}]");
            sb.Append(';');

            for (int i = 0; i < n; i++)
            {
                SpeedSegment s = kept[i];
                sb.Append($"[vsrc{i}]trim=start={Num(s.Start)}:end={Num(s.End)},setpts=(PTS-STARTPTS)/{Num(s.Speed)}[vseg{i}];");
            }

            for (int i = 0; i < n; i++) sb.Append($"[vseg{i}]");
            sb.Append($"concat=n={n}:v=1:a=0{videoOut}");
        }

        if (hasAudio)
        {
            sb.Append(';');
            if (n == 1 && segments.Count == 1)
            {
                sb.Append($"[0:a]{AtempoChain(kept[0].Speed)}[a]");
            }
            else if (n == 1)
            {
                sb.Append($"[0:a]atrim=start={Num(kept[0].Start)}:end={Num(kept[0].End)},asetpts=PTS-STARTPTS,{AtempoChain(kept[0].Speed)}[a]");
            }
            else
            {
                sb.Append("[0:a]asplit=").Append(n);
                for (int i = 0; i < n; i++) sb.Append($"[asrc{i}]");
                sb.Append(';');

                for (int i = 0; i < n; i++)
                {
                    SpeedSegment s = kept[i];
                    sb.Append($"[asrc{i}]atrim=start={Num(s.Start)}:end={Num(s.End)},asetpts=PTS-STARTPTS,{AtempoChain(s.Speed)}[aseg{i}];");
                }

                for (int i = 0; i < n; i++) sb.Append($"[aseg{i}]");
                sb.Append($"concat=n={n}:v=0:a=1[a]");
            }
        }

        return sb.ToString();
    }

    private static string Num(double value) => value.ToString("0.#####", CultureInfo.InvariantCulture);
}

// Splitting/reconciliation helpers kept separate from the record so the view model stays lean. Segments
// are always contiguous and cover exactly [start, end].
public static class SpeedSegmentEditor
{
    public const double MinSegmentSeconds = 0.15;

    public static List<SpeedSegment> Single(double start, double end) => [new SpeedSegment(start, end, 1)];

    // Splits whichever segment contains <time> into two, preserving speed. No-op if the split would create
    // a sliver shorter than MinSegmentSeconds on either side.
    public static bool Split(List<SpeedSegment> segments, double time)
    {
        for (int i = 0; i < segments.Count; i++)
        {
            SpeedSegment s = segments[i];
            if (time <= s.Start + MinSegmentSeconds || time >= s.End - MinSegmentSeconds) continue;
            if (time > s.Start && time < s.End)
            {
                segments[i] = s with { End = time };
                segments.Insert(i + 1, s with { Start = time });
                return true;
            }
        }
        return false;
    }

    // Re-fits the segments to a (possibly changed) trim range: drops what falls outside, clamps the
    // boundary segments, and guarantees full contiguous coverage of [start, end].
    public static List<SpeedSegment> Reconcile(IReadOnlyList<SpeedSegment> segments, double start, double end)
    {
        if (end - start < MinSegmentSeconds || segments.Count == 0) return Single(start, end);

        List<SpeedSegment> result = [];
        foreach (SpeedSegment s in segments)
        {
            double clampedStart = Math.Max(start, s.Start);
            double clampedEnd = Math.Min(end, s.End);
            if (clampedEnd - clampedStart > 1e-4) result.Add(s with { Start = clampedStart, End = clampedEnd });
        }

        if (result.Count == 0) return Single(start, end);

        // Snap the ends so the lane always fills the selection even when trim grew past the old bounds.
        result[0] = result[0] with { Start = start };
        result[^1] = result[^1] with { End = end };

        // Stitch any gaps left by clamping so the segments stay perfectly contiguous.
        for (int i = 1; i < result.Count; i++)
        {
            if (result[i].Start > result[i - 1].End + 1e-4)
            {
                result[i] = result[i] with { Start = result[i - 1].End };
            }
        }

        return result;
    }

    public static int IndexAt(IReadOnlyList<SpeedSegment> segments, double time)
    {
        for (int i = 0; i < segments.Count; i++)
        {
            if (time >= segments[i].Start && time < segments[i].End) return i;
        }
        return segments.Count - 1;
    }

    // Earliest playable moment at or after <time>, hopping over removed clips. -1 when nothing
    // playable remains.
    public static double NextKeptTime(IReadOnlyList<SpeedSegment> segments, double time)
    {
        foreach (SpeedSegment s in segments)
        {
            if (s.Removed) continue;
            if (time < s.End - 1e-4) return Math.Max(time, s.Start);
        }
        return -1;
    }

    // Carves the given (sorted, non-overlapping) time ranges out of the lane and runs <apply> on the
    // carved slices, e.g. to fast-forward or cut every idle span in one pass. Boundaries that would
    // leave a sliver shorter than MinSegmentSeconds are snapped to the nearest segment edge.
    public static List<SpeedSegment> ApplyRanges(
        IReadOnlyList<SpeedSegment> segments,
        IReadOnlyList<(double Start, double End)> ranges,
        Func<SpeedSegment, SpeedSegment> apply)
    {
        List<SpeedSegment> result = [];
        foreach (SpeedSegment s in segments)
        {
            double cursor = s.Start;
            foreach ((double rangeStart, double rangeEnd) in ranges)
            {
                double sliceStart = Math.Max(cursor, rangeStart);
                double sliceEnd = Math.Min(s.End, rangeEnd);
                if (sliceEnd - sliceStart < MinSegmentSeconds) continue;

                if (sliceStart - cursor < MinSegmentSeconds)
                {
                    sliceStart = cursor;
                }
                else
                {
                    result.Add(s with { Start = cursor, End = sliceStart });
                }

                if (s.End - sliceEnd < MinSegmentSeconds) sliceEnd = s.End;

                result.Add(apply(s with { Start = sliceStart, End = sliceEnd }));
                cursor = sliceEnd;
            }

            if (s.End - cursor > 1e-4) result.Add(s with { Start = cursor, End = s.End });
        }
        return result;
    }
}
