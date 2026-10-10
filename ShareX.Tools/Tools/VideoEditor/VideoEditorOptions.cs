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

namespace ShareX.Tools;

public sealed class VideoEditorOptions
{
    public bool AutoOpenFolder { get; set; } = true;
    public double AutoZoomAmount { get; set; } = 1.8;
    public double AutoZoomSmoothness { get; set; } = 0.5;
    public double IdleThresholdSeconds { get; set; } = 2;
    public bool EffectClickRipples { get; set; }
    public bool PhoneTapMode { get; set; }
    public double PhoneTapSize { get; set; } = 1.5;
    public double PhoneTapBrightness { get; set; } = 1.3;
    public double PhoneTapImpact { get; set; } = 0.4;
    public bool EffectSpotlight { get; set; }
    public double SpotlightSize { get; set; } = 0.22;
    public bool EffectStudioBackground { get; set; }
    public string StudioStyle { get; set; } = "Midnight";
    public bool EffectProgressBar { get; set; }
    // When on, new recordings are captured without the system cursor and a smoothed replacement is
    // drawn from the recorded mouse track instead.
    public bool SmoothCursor { get; set; }
    public string CursorStyle { get; set; } = "Classic";
    public double CursorSize { get; set; } = 1.5;
    public double CursorSmoothing { get; set; } = 0.5;
    public int ExportMaxHeight { get; set; } = 0;
    public int ExportCrf { get; set; } = 18;
}
