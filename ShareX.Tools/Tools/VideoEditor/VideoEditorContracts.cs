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

public sealed record VideoEditorMediaInfo(
    TimeSpan Duration,
    int Width,
    int Height,
    double FramesPerSecond,
    bool HasAudio = false,
    string? ErrorMessage = null)
{
    public bool IsValid => Duration > TimeSpan.Zero && Width > 0 && Height > 0;
}

public sealed record VideoEditorExportRequest(
    string Arguments,
    string OutputFilePath,
    TimeSpan Duration,
    bool AutoOpenFolder,
    IReadOnlyList<string>? TempFiles = null);

public sealed record VideoEditorExportResult(bool Succeeded, bool WasCancelled, string? ErrorMessage = null);

// A decoded, downscaled run of frames used for smooth in-editor playback. Frames are evenly spaced
// starting at Start with 1/Fps between them.
public sealed record VideoEditorPreviewSequence(IReadOnlyList<byte[]> Frames, double Fps, TimeSpan Start);

public delegate Task<VideoEditorMediaInfo> VideoEditorProbeHandler(string filePath, CancellationToken cancellationToken);
public delegate Task<byte[]?> VideoEditorPreviewHandler(string filePath, TimeSpan position, CancellationToken cancellationToken);
public delegate Task<VideoEditorPreviewSequence?> VideoEditorSequenceHandler(
    string filePath,
    TimeSpan start,
    TimeSpan end,
    double fps,
    int maxWidth,
    CancellationToken cancellationToken);
public delegate Task<VideoEditorExportResult> VideoEditorExportHandler(
    VideoEditorExportRequest request,
    IProgress<double> progress,
    CancellationToken cancellationToken);

public sealed record VideoEditorServices(
    VideoEditorProbeHandler Probe,
    VideoEditorPreviewHandler GetPreview,
    VideoEditorSequenceHandler GetPreviewSequence,
    VideoEditorExportHandler Export);
