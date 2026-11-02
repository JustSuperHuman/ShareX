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

using System.Drawing;
using Bitmap = Avalonia.Media.Imaging.Bitmap;

namespace ShareX.Tools;

public enum RecordingTargetKind
{
    Display,
    Window,
    Area
}

public enum CameraBubbleShape
{
    Round,
    Square,
    Wide
}

// What the "New recording" window remembers between sessions.
public sealed class RecordingStudioOptions
{
    public RecordingTargetKind TargetKind { get; set; } = RecordingTargetKind.Display;
    public string CameraDevice { get; set; } = "";
    public string MicrophoneDevice { get; set; } = "";
    public bool SystemAudio { get; set; }
    public bool UploadToCap { get; set; }
    public int SetupX { get; set; } = int.MinValue;
    public int SetupY { get; set; } = int.MinValue;
    public int CameraSize { get; set; } = 230;
    public double CameraZoom { get; set; } = 1;
    public CameraBubbleShape CameraShape { get; set; } = CameraBubbleShape.Round;
    public bool CameraMirrored { get; set; } = true;
    public int CameraX { get; set; } = int.MinValue;
    public int CameraY { get; set; } = int.MinValue;
}

// A top-level window the user can record, described by the host application.
public sealed record RecordingWindowInfo(IntPtr Handle, string Title, string AppName, Rectangle Bounds, Bitmap? Icon);

// Everything the recorder needs once the user presses Start. Region is empty for Area, where the
// host lets the user draw it.
public sealed record RecordingStudioRequest(
    RecordingTargetKind Kind,
    Rectangle Region,
    IntPtr WindowHandle,
    IReadOnlyList<string> AudioDevices,
    bool UploadToCap);

// Host-provided hooks, so the window itself stays free of capture and upload code.
public sealed record RecordingStudioServices(
    string FFmpegPath,
    Func<IReadOnlyList<RecordingWindowInfo>> GetWindows,
    Action<RecordingStudioRequest> Start,
    bool CapConfigured,
    Action OpenCapSettings,
    Action OpenAudioDeviceDownload);

public sealed record RecordingDisplayItem(string Name, Rectangle Bounds, int RefreshRate, bool Primary)
{
    public string Title => Primary ? $"{Name} (main)" : Name;
    public string Detail => RefreshRate > 0 ? $"{Bounds.Width} × {Bounds.Height} · {RefreshRate} Hz" : $"{Bounds.Width} × {Bounds.Height}";
}

public sealed record RecordingWindowItem(RecordingWindowInfo Info)
{
    public string Title => string.IsNullOrWhiteSpace(Info.Title) ? Info.AppName : Info.Title;
    public string Detail => $"{Info.AppName} · {Info.Bounds.Width} × {Info.Bounds.Height}";
    public Bitmap? Icon => Info.Icon;
}

// A camera or microphone entry. An empty Device is the "none" choice.
public sealed record RecordingDeviceItem(string Device, string Title, string Detail)
{
    public bool IsNone => string.IsNullOrEmpty(Device);
}
