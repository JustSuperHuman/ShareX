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
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShareX.HelpersLib;
using ShareX.Tools.Controls;
using System.Diagnostics;
using System.Globalization;

namespace ShareX.Tools;

public sealed record VideoCropAspectItem(string DisplayName, double? Ratio, bool IsOriginal = false);

public sealed record VideoExportSizeItem(string DisplayName, int MaxHeight);

public sealed record VideoExportQualityItem(string DisplayName, int Crf);

public sealed record VideoStudioStyleItem(string DisplayName, (byte R, byte G, byte B) Top, (byte R, byte G, byte B) Bottom)
{
    // The diagonal gradient shown on the style's picker card — same direction the export renders.
    public Avalonia.Media.IBrush PreviewBrush { get; } = new Avalonia.Media.LinearGradientBrush
    {
        StartPoint = new Avalonia.RelativePoint(0, 0, Avalonia.RelativeUnit.Relative),
        EndPoint = new Avalonia.RelativePoint(1, 1, Avalonia.RelativeUnit.Relative),
        GradientStops =
        {
            new Avalonia.Media.GradientStop(Avalonia.Media.Color.FromRgb(Top.R, Top.G, Top.B), 0),
            new Avalonia.Media.GradientStop(Avalonia.Media.Color.FromRgb(Bottom.R, Bottom.G, Bottom.B), 1)
        }
    };
}

public sealed partial class VideoEditorViewModel : ViewModelBase, IDisposable
{
    public static IReadOnlyList<VideoCropAspectItem> CropAspects { get; } =
    [
        new("Free", null),
        new("Original", null, true),
        new("16:9", 16d / 9d),
        new("4:3", 4d / 3d),
        new("1:1", 1d),
        new("9:16", 9d / 16d)
    ];

    public static IReadOnlyList<double> PlaybackSpeeds { get; } = [0.5, 1, 1.5, 2];

    public static IReadOnlyList<double> SpeedPresets { get; } = [0.25, 0.5, 1, 1.5, 2, 4];

    public static IReadOnlyList<VideoExportSizeItem> ExportSizes { get; } =
    [
        new("Original size", 0),
        new("Up to 1080p", 1080),
        new("Up to 720p", 720),
        new("Up to 480p", 480)
    ];

    public static IReadOnlyList<VideoExportQualityItem> ExportQualities { get; } =
    [
        new("High quality", 18),
        new("Balanced", 23),
        new("Smaller file", 28)
    ];

    public static IReadOnlyList<VideoStudioStyleItem> StudioStyles { get; } =
    [
        new("Midnight", (49, 46, 129), (15, 23, 42)),
        new("Ocean", (14, 165, 233), (30, 58, 138)),
        new("Sunset", (249, 115, 22), (124, 58, 237)),
        new("Forest", (16, 185, 129), (6, 78, 59)),
        new("Graphite", (63, 63, 70), (24, 24, 27)),
        new("Aurora", (34, 211, 238), (99, 102, 241)),
        new("Candy", (244, 114, 182), (251, 146, 60)),
        new("Crimson", (244, 63, 94), (76, 5, 25)),
        new("Lavender", (196, 181, 253), (109, 40, 217)),
        new("Gold", (251, 191, 36), (146, 64, 14)),
        new("Mint", (110, 231, 183), (13, 148, 136)),
        new("Steel", (148, 163, 184), (51, 65, 85))
    ];

    public static IReadOnlyList<VideoCursorStyleItem> CursorStyles { get; } =
        [.. VideoCursorArt.Styles.Select(s => new VideoCursorStyleItem(s.Id, s.DisplayName))];

    private const double IdleFastForwardSpeed = 4;

    private const double PlaybackFrameRate = 15;
    private const int PlaybackMaxWidth = 960;

    private readonly VideoEditorOptions _options;
    private readonly VideoEditorServices _services;
    private CancellationTokenSource? _operationCancellation;
    private CancellationTokenSource? _previewCancellation;
    private bool _adjustingTime;
    private bool _applyingCropPreset;
    private bool _disposed;

    private readonly DispatcherTimer _playbackTimer;
    private readonly Stopwatch _playbackClock = new();
    private double _lastTickSeconds;
    private byte[][]? _playbackFrames;
    private double _playbackSequenceFps;
    private double _playbackSequenceStart;
    private double _playbackSequenceEnd = -1;
    private int _lastShownFrameIndex = -1;
    private CancellationTokenSource? _sequenceCancellation;

    private ScreenRecordingMotionData? _motionData;
    private AutoZoomPlan _autoZoomPlan = AutoZoomPlan.Empty;
    private VideoCursorPath? _cursorPath;
    private VideoCursorSprite? _cursorSprite;

    // The speed segments always cover [InPoint, OutPoint] contiguously; each plays at its own multiplier.
    private List<SpeedSegment> _segments = [];
    private bool _hasAudio;

    // Snapshots of the segment lane taken before every clip edit, so Ctrl+Z can walk back through
    // splits, speed changes, cuts, and idle passes.
    private readonly Stack<(List<SpeedSegment> Segments, int SelectedIndex)> _undoStack = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InputFileDisplay))]
    private string _inputFilePath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputFilePath))]
    private string _outputFolderPath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputFilePath))]
    private string _outputFileName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyPropertyChangedFor(nameof(CanExport))]
    private bool _isBusy;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _statusText = "Choose a screen recording to get started.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoPreview))]
    private Bitmap? _previewImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceInfoText))]
    [NotifyPropertyChangedFor(nameof(CropMaximumX))]
    [NotifyPropertyChangedFor(nameof(CropMaximumWidth))]
    private int _sourceWidth;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceInfoText))]
    [NotifyPropertyChangedFor(nameof(CropMaximumY))]
    [NotifyPropertyChangedFor(nameof(CropMaximumHeight))]
    private int _sourceHeight;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceInfoText))]
    private double _framesPerSecond;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationText))]
    [NotifyPropertyChangedFor(nameof(CanExport))]
    private double _duration;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartTimeText))]
    [NotifyPropertyChangedFor(nameof(SelectionDurationText))]
    private double _inPoint;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EndTimeText))]
    [NotifyPropertyChangedFor(nameof(SelectionDurationText))]
    private double _outPoint;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewTimeText))]
    private double _previewPosition;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CropX))]
    [NotifyPropertyChangedFor(nameof(CropY))]
    [NotifyPropertyChangedFor(nameof(CropWidth))]
    [NotifyPropertyChangedFor(nameof(CropHeight))]
    [NotifyPropertyChangedFor(nameof(CropSizeText))]
    private Rect _cropRect;

    [ObservableProperty]
    private VideoCropAspectItem _selectedCropAspect = CropAspects[1];

    [ObservableProperty]
    private bool _autoOpenFolder;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private double _selectedPlaybackSpeed = 1;

    [ObservableProperty]
    private bool _isCropEditable = true;

    [ObservableProperty]
    private Rect _cameraRect;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoZoomAvailable))]
    [NotifyPropertyChangedFor(nameof(SpotlightPreviewActive))]
    [NotifyPropertyChangedFor(nameof(RipplePreviewActive))]
    private bool _hasMotionData;

    [ObservableProperty]
    private bool _autoZoomEnabled;

    [ObservableProperty]
    private double _autoZoomAmount = 1.8;

    [ObservableProperty]
    private double _autoZoomSmoothness = 0.5;

    [ObservableProperty]
    private string _autoZoomStatusText = "";

    [ObservableProperty]
    private IReadOnlyList<TimelineZoomRegion> _zoomRegions = [];

    [ObservableProperty]
    private IReadOnlyList<TimelineSpeedSegment> _speedBands = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedSegment))]
    [NotifyPropertyChangedFor(nameof(SelectedSegmentText))]
    [NotifyPropertyChangedFor(nameof(SelectedSpeed))]
    [NotifyPropertyChangedFor(nameof(SelectedSegmentRemoved))]
    private int _selectedSegmentIndex = -1;

    [ObservableProperty]
    private IReadOnlyList<double> _clickMarkers = [];

    [ObservableProperty]
    private IReadOnlyList<VideoClickHighlight> _clickHighlights = [];

    [ObservableProperty]
    private bool _effectClickRipples;

    [ObservableProperty]
    private bool _phoneTapMode;

    [ObservableProperty]
    private double _phoneTapSize = 1.5;

    [ObservableProperty]
    private double _phoneTapBrightness = 1.3;

    [ObservableProperty]
    private double _phoneTapImpact = 0.4;

    [ObservableProperty]
    private bool _effectSpotlight;

    [ObservableProperty]
    private double _spotlightSize = 0.22;

    [ObservableProperty]
    private Point _spotlightCenter = new(0.5, 0.5);

    [ObservableProperty]
    private bool _effectStudioBackground;

    [ObservableProperty]
    private VideoStudioStyleItem _selectedStudioStyle = StudioStyles[0];

    [ObservableProperty]
    private bool _effectProgressBar;

    [ObservableProperty]
    private bool _smoothCursorEnabled;

    // True when the loaded recording was captured without the system cursor, so one can be drawn in.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PhoneTapAvailable))]
    [NotifyPropertyChangedFor(nameof(PhoneTapActive))]
    private bool _cursorReplaceable;

    [ObservableProperty]
    private VideoCursorStyleItem _selectedCursorStyle = CursorStyles[0];

    [ObservableProperty]
    private double _cursorSize = 1.5;

    [ObservableProperty]
    private double _cursorSmoothing = 0.5;

    [ObservableProperty]
    private Bitmap? _cursorImage;

    [ObservableProperty]
    private Point _cursorHotspot;

    [ObservableProperty]
    private Point _cursorPosition = new(0.5, 0.5);

    [ObservableProperty]
    private double _progressBarFraction;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IdleThresholdText))]
    private double _idleThresholdSeconds;

    [ObservableProperty]
    private VideoExportSizeItem _selectedExportSize = ExportSizes[0];

    [ObservableProperty]
    private VideoExportQualityItem _selectedExportQuality = ExportQualities[0];

    public VideoEditorViewModel(VideoEditorOptions options, VideoEditorServices services, string? inputFilePath = null)
    {
        _options = options;
        _services = services;
        _autoOpenFolder = options.AutoOpenFolder;
        _autoZoomAmount = options.AutoZoomAmount;
        _autoZoomSmoothness = options.AutoZoomSmoothness;
        _idleThresholdSeconds = Math.Clamp(options.IdleThresholdSeconds, 0.5, 10);
        _effectClickRipples = options.EffectClickRipples;
        _phoneTapMode = options.PhoneTapMode;
        _phoneTapSize = Math.Clamp(options.PhoneTapSize, 0.5, 3);
        _phoneTapBrightness = Math.Clamp(options.PhoneTapBrightness, 0.5, 2);
        _phoneTapImpact = Math.Clamp(options.PhoneTapImpact, 0, 1);
        _effectSpotlight = options.EffectSpotlight;
        _spotlightSize = Math.Clamp(options.SpotlightSize, 0.1, 0.4);
        _effectStudioBackground = options.EffectStudioBackground;
        _selectedStudioStyle = StudioStyles.FirstOrDefault(s => s.DisplayName == options.StudioStyle) ?? StudioStyles[0];
        _effectProgressBar = options.EffectProgressBar;
        _smoothCursorEnabled = options.SmoothCursor;
        _selectedCursorStyle = CursorStyles.FirstOrDefault(s => s.Id == options.CursorStyle) ?? CursorStyles[0];
        _cursorSize = Math.Clamp(options.CursorSize, 0.75, 3);
        _cursorSmoothing = Math.Clamp(options.CursorSmoothing, 0, 1);
        _selectedExportSize = ExportSizes.FirstOrDefault(s => s.MaxHeight == options.ExportMaxHeight) ?? ExportSizes[0];
        _selectedExportQuality = ExportQualities.FirstOrDefault(q => q.Crf == options.ExportCrf) ?? ExportQualities[0];
        InputFilePath = inputFilePath ?? string.Empty;

        _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0 / PlaybackFrameRate) };
        _playbackTimer.Tick += OnPlaybackTick;
    }

    public Func<string, Task<string?>>? SelectInputFileRequested { get; set; }
    public Func<string, Task<string?>>? SelectOutputFolderRequested { get; set; }

    public bool IsIdle => !IsBusy;
    public bool CanExport => IsIdle && Duration > 0 && OutPoint > InPoint && SourceWidth > 0 && SourceHeight > 0 && HasKeptClip;
    private bool HasKeptClip => _segments.Count == 0 || _segments.Any(s => !s.Removed);
    public bool HasNoPreview => PreviewImage == null;
    public bool AutoZoomAvailable => HasMotionData;
    public bool SpotlightPreviewActive => EffectSpotlight && HasMotionData;
    public bool PhoneTapAvailable => CursorReplaceable;
    public bool PhoneTapActive => PhoneTapMode && CursorReplaceable;
    public bool RipplePreviewActive => (EffectClickRipples || PhoneTapActive) && HasMotionData;
    public Avalonia.Media.Color StudioTopColor => Avalonia.Media.Color.FromRgb(SelectedStudioStyle.Top.R, SelectedStudioStyle.Top.G, SelectedStudioStyle.Top.B);
    public Avalonia.Media.Color StudioBottomColor => Avalonia.Media.Color.FromRgb(SelectedStudioStyle.Bottom.R, SelectedStudioStyle.Bottom.G, SelectedStudioStyle.Bottom.B);
    public bool HasCursorNotice => CursorNoticeText.Length > 0;

    public string CursorNoticeText
    {
        get
        {
            if (CursorReplaceable)
            {
                return SmoothCursorEnabled ? string.Empty : "This recording was captured without the Windows cursor. Leave this off to export it with no cursor at all.";
            }

            if (!SmoothCursorEnabled) return string.Empty;

            return SourceWidth > 0
                ? "The Windows cursor is already part of this recording, so it stays as it is. Recordings you make from now on get this cursor instead."
                : "Recordings you make from now on get this cursor instead of the Windows one.";
        }
    }

    public string InputFileDisplay => string.IsNullOrWhiteSpace(InputFilePath) ? "No file selected" : InputFilePath;
    public string SourceInfoText => SourceWidth <= 0 ? string.Empty : $"{SourceWidth} × {SourceHeight}  •  {FramesPerSecond:0.##} fps";
    public string DurationText => FormatTime(Duration);
    public string PreviewTimeText => FormatTime(PreviewPosition);
    public string SelectionDurationText => FormatTime(Math.Max(0, OutPoint - InPoint));
    public string CropSizeText => CropWidth > 0 && CropHeight > 0 ? $"{CropWidth:0} × {CropHeight:0}" : string.Empty;

    public bool HasSelectedSegment => SelectedSegmentIndex >= 0 && SelectedSegmentIndex < _segments.Count;
    public double SelectedSpeed => HasSelectedSegment ? _segments[SelectedSegmentIndex].Speed : 1;
    public bool SelectedSegmentRemoved => HasSelectedSegment && _segments[SelectedSegmentIndex].Removed;
    public bool HasSpeedEdits => VideoSpeedGraph.HasEdits(_segments);
    public bool CanUndo => _undoStack.Count > 0;
    public string IdleThresholdText => $"{IdleThresholdSeconds:0.#}s";

    public string SelectedSegmentText
    {
        get
        {
            if (!HasSelectedSegment) return "Click a clip below, or split at the playhead to start.";
            SpeedSegment segment = _segments[SelectedSegmentIndex];
            string state = segment.Removed ? "cut" : FormatSpeed(segment.Speed);
            return $"Clip {SelectedSegmentIndex + 1} of {_segments.Count}  •  {FormatTime(segment.SourceDuration)}  •  {state}";
        }
    }

    public string SpeedSummaryText
    {
        get
        {
            if (!HasSpeedEdits) return _segments.Count > 1 ? "Set a speed on any clip, or cut it." : "Split the clip, then speed up, slow down, or cut each piece.";
            return $"Output length ≈ {FormatTime(VideoSpeedGraph.OutputDuration(GetRelativeSegments(Math.Max(0.05, OutPoint - InPoint))))}";
        }
    }

    public double CropX { get => CropRect.X; set => SetCrop(value, CropRect.Y, CropRect.Width, CropRect.Height); }
    public double CropY { get => CropRect.Y; set => SetCrop(CropRect.X, value, CropRect.Width, CropRect.Height); }
    public double CropWidth { get => CropRect.Width; set => SetCrop(CropRect.X, CropRect.Y, value, CropRect.Height); }
    public double CropHeight { get => CropRect.Height; set => SetCrop(CropRect.X, CropRect.Y, CropRect.Width, value); }
    public double CropMaximumX => Math.Max(0, SourceWidth - 2);
    public double CropMaximumY => Math.Max(0, SourceHeight - 2);
    public double CropMaximumWidth => Math.Max(2, SourceWidth);
    public double CropMaximumHeight => Math.Max(2, SourceHeight);

    public string StartTimeText
    {
        get => FormatTime(InPoint);
        set
        {
            if (TryParseTime(value, out double seconds)) InPoint = seconds;
        }
    }

    public string EndTimeText
    {
        get => FormatTime(OutPoint);
        set
        {
            if (TryParseTime(value, out double seconds)) OutPoint = seconds;
        }
    }

    public string OutputFilePath
    {
        get
        {
            if (string.IsNullOrWhiteSpace(OutputFolderPath) || string.IsNullOrWhiteSpace(OutputFileName)) return string.Empty;
            string path = Path.Combine(OutputFolderPath, OutputFileName);
            return Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase) ? path : Path.ChangeExtension(path, ".mp4");
        }
    }

    public async Task InitializeAsync()
    {
        if (!string.IsNullOrWhiteSpace(InputFilePath))
        {
            await LoadInputAsync(InputFilePath);
        }
    }

    [RelayCommand]
    private async Task BrowseInputAsync()
    {
        if (!IsIdle || SelectInputFileRequested == null) return;
        string? filePath = await SelectInputFileRequested("Select screen recording");
        if (!string.IsNullOrWhiteSpace(filePath)) await LoadInputAsync(filePath);
    }

    [RelayCommand]
    private async Task BrowseOutputFolderAsync()
    {
        if (!IsIdle || SelectOutputFolderRequested == null) return;
        string? folderPath = await SelectOutputFolderRequested("Select output folder");
        if (!string.IsNullOrWhiteSpace(folderPath)) OutputFolderPath = folderPath;
    }

    public async Task LoadInputAsync(string filePath)
    {
        if (!File.Exists(filePath) || !IsIdle) return;

        StopPlayback();
        CancelPreview();
        InvalidatePlaybackSequence();
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();
        IsBusy = true;
        Progress = 0;
        StatusText = "Reading recording...";

        try
        {
            VideoEditorMediaInfo info = await _services.Probe(filePath, _operationCancellation.Token);
            if (!info.IsValid)
            {
                StatusText = info.ErrorMessage ?? "ShareX could not read this video.";
                return;
            }

            InputFilePath = filePath;
            SourceWidth = info.Width;
            SourceHeight = info.Height;
            FramesPerSecond = info.FramesPerSecond;
            _hasAudio = info.HasAudio;
            Duration = info.Duration.TotalSeconds;
            InPoint = 0;
            OutPoint = Duration;
            PreviewPosition = 0;
            _undoStack.Clear();
            OnPropertyChanged(nameof(CanUndo));
            InitializeSpeedSegments();
            ApplyCropPreset(CropAspects[1]);
            OutputFolderPath = Path.GetDirectoryName(filePath) ?? string.Empty;
            OutputFileName = $"{Path.GetFileNameWithoutExtension(filePath)}-edited.mp4";

            LoadMotionData(filePath);

            StatusText = HasMotionData
                ? "Motion track found — enable Auto-zoom to make it cinematic."
                : "Drag the timeline handles to trim, and drag the frame handles to crop.";

            await LoadPreviewAsync(0, immediate: true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusText = $"Could not open video: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void LoadMotionData(string filePath)
    {
        AutoZoomEnabled = false;
        _motionData = ScreenRecordingMotionData.Load(filePath);
        HasMotionData = _motionData is { HasSamples: true };
        ClickMarkers = HasMotionData && _motionData?.Clicks != null
            ? [.. _motionData.Clicks.Select(c => (double)c.T)]
            : [];
        ClickHighlights = HasMotionData && _motionData?.Clicks != null
            ? [.. _motionData.Clicks.Select(c => new VideoClickHighlight(c.T, c.X, c.Y))]
            : [];
        CursorReplaceable = HasMotionData && _motionData!.CursorHidden;
        PhoneTapMode = CursorReplaceable && (_motionData!.PhoneTapMode || _options.PhoneTapMode);
        RebuildCursorPath();
        RefreshCursorSprite();
        RebuildAutoZoomPlan();
    }

    private void RebuildCursorPath()
    {
        _cursorPath = CursorReplaceable ? VideoCursorPath.Build(_motionData, CursorSmoothing) : null;
        UpdateCursorPosition();
    }

    // Re-renders the cursor at the size this recording needs; the same sprite feeds the preview and the export.
    private void RefreshCursorSprite()
    {
        Bitmap? previous = CursorImage;

        if (SmoothCursorEnabled && CursorReplaceable && !PhoneTapActive && SourceWidth > 0 && SourceHeight > 0)
        {
            _cursorSprite = VideoCursorArt.Render(SelectedCursorStyle.Id, VideoCursorArt.GetSpriteSize(SourceWidth, SourceHeight, CursorSize));
            CursorHotspot = new Point(_cursorSprite.HotspotX, _cursorSprite.HotspotY);
            CursorImage = new Bitmap(new MemoryStream(_cursorSprite.Png, writable: false));
        }
        else
        {
            _cursorSprite = null;
            CursorImage = null;
        }

        previous?.Dispose();
        OnPropertyChanged(nameof(CursorNoticeText));
        OnPropertyChanged(nameof(HasCursorNotice));
    }

    private void UpdateCursorPosition()
    {
        if (_cursorPath == null) return;
        (double x, double y) = _cursorPath.Sample(PreviewPosition);
        CursorPosition = new Point(x, y);
    }

    [RelayCommand]
    private void ResetTrim()
    {
        InPoint = 0;
        OutPoint = Duration;
        PreviewPosition = 0;
    }

    [RelayCommand]
    private void SetStart()
    {
        InPoint = Math.Min(PreviewPosition, Math.Max(0, OutPoint - 0.05));
    }

    [RelayCommand]
    private void SetEnd()
    {
        OutPoint = Math.Max(PreviewPosition, Math.Min(Duration, InPoint + 0.05));
    }

    [RelayCommand]
    private void StepBackward()
    {
        StopPlayback();
        PreviewPosition = Math.Max(InPoint, PreviewPosition - GetFrameStep());
    }

    [RelayCommand]
    private void StepForward()
    {
        StopPlayback();
        PreviewPosition = Math.Min(OutPoint, PreviewPosition + GetFrameStep());
    }

    [RelayCommand]
    private void GoToStart()
    {
        StopPlayback();
        PreviewPosition = InPoint;
    }

    [RelayCommand]
    private void GoToEnd()
    {
        StopPlayback();
        PreviewPosition = OutPoint;
    }

    public void JumpBySeconds(double seconds)
    {
        StopPlayback();
        PreviewPosition = Math.Clamp(PreviewPosition + seconds, InPoint, OutPoint);
    }

    [RelayCommand]
    private void ResetCrop() => ApplyCropPreset(CropAspects[1]);

    [RelayCommand]
    private void SplitAtPlayhead()
    {
        if (Duration <= 0) return;
        PushUndoSnapshot();
        if (SpeedSegmentEditor.Split(_segments, PreviewPosition))
        {
            SelectedSegmentIndex = SpeedSegmentEditor.IndexAt(_segments, PreviewPosition);
            PublishSegments();
            StatusText = "Split added — choose a speed for the highlighted clip, or cut it.";
        }
        else
        {
            DiscardUndoSnapshot();
            StatusText = "Move the playhead into the middle of a clip to split it.";
        }
    }

    [RelayCommand]
    private void SetSegmentSpeed(string? speedText)
    {
        if (!double.TryParse(speedText, NumberStyles.Float, CultureInfo.InvariantCulture, out double speed)) return;
        if (_segments.Count == 0 || Duration <= 0) return;

        int index = HasSelectedSegment ? SelectedSegmentIndex : SpeedSegmentEditor.IndexAt(_segments, PreviewPosition);
        if (index < 0 || index >= _segments.Count) return;

        PushUndoSnapshot();
        _segments[index] = _segments[index].WithSpeed(speed);
        SelectedSegmentIndex = index;
        PublishSegments();
        StatusText = VideoSpeedGraph.IsUnity(speed)
            ? "Clip set to normal speed."
            : $"Clip set to {FormatSpeed(speed)} — the preview follows along.";
    }

    [RelayCommand]
    private void ToggleRemoveSegment()
    {
        if (_segments.Count == 0 || Duration <= 0) return;

        int index = HasSelectedSegment ? SelectedSegmentIndex : SpeedSegmentEditor.IndexAt(_segments, PreviewPosition);
        if (index < 0 || index >= _segments.Count) return;

        SpeedSegment segment = _segments[index];
        if (!segment.Removed && _segments.Count(s => !s.Removed) <= 1)
        {
            StatusText = "At least one clip has to remain in the video.";
            return;
        }

        PushUndoSnapshot();
        _segments[index] = segment.WithRemoved(!segment.Removed);
        SelectedSegmentIndex = index;
        PublishSegments();
        StatusText = segment.Removed
            ? "Clip restored."
            : "Clip cut — it will be skipped in the preview and the export.";
    }

    [RelayCommand]
    private void FastForwardIdle() => ApplyIdle(remove: false);

    [RelayCommand]
    private void CutIdle() => ApplyIdle(remove: true);

    private void ApplyIdle(bool remove)
    {
        if (_motionData == null || Duration <= 0) return;

        IReadOnlyList<(double Start, double End)> spans = IdleDetector.FindIdleSpans(_motionData, InPoint, OutPoint, IdleThresholdSeconds);
        if (spans.Count == 0)
        {
            StatusText = $"No idle stretches longer than {IdleThresholdText} found.";
            return;
        }

        PushUndoSnapshot();
        List<SpeedSegment> reconciled = SpeedSegmentEditor.Reconcile(_segments, InPoint, OutPoint);
        _segments = SpeedSegmentEditor.ApplyRanges(reconciled, spans,
            s => remove ? s.WithRemoved(true) : s.WithSpeed(IdleFastForwardSpeed));
        SelectedSegmentIndex = -1;
        PublishSegments();

        double idleSeconds = IdleDetector.TotalSeconds(spans);
        double savedSeconds = remove ? idleSeconds : idleSeconds - idleSeconds / IdleFastForwardSpeed;
        string action = remove ? "Cut" : $"Fast-forwarded ({FormatSpeed(IdleFastForwardSpeed)})";
        StatusText = $"{action} {spans.Count} idle stretch{(spans.Count == 1 ? "" : "es")} — the export gets ≈ {FormatTime(savedSeconds)} shorter. Undo with Ctrl+Z.";
    }

    [RelayCommand]
    private void UndoSegmentEdit()
    {
        if (_undoStack.Count == 0) return;
        (List<SpeedSegment> segments, int selectedIndex) = _undoStack.Pop();
        _segments = SpeedSegmentEditor.Reconcile(segments, InPoint, OutPoint);
        SelectedSegmentIndex = selectedIndex < _segments.Count ? selectedIndex : -1;
        PublishSegments();
        OnPropertyChanged(nameof(CanUndo));
        StatusText = "Clip change undone.";
    }

    [RelayCommand]
    private void ResetSpeeds()
    {
        PushUndoSnapshot();
        InitializeSpeedSegments();
        StatusText = "Speed and cut changes cleared.";
    }

    private void PushUndoSnapshot()
    {
        _undoStack.Push(([.. _segments], SelectedSegmentIndex));
        OnPropertyChanged(nameof(CanUndo));
    }

    private void DiscardUndoSnapshot()
    {
        if (_undoStack.Count > 0) _undoStack.Pop();
        OnPropertyChanged(nameof(CanUndo));
    }

    private void InitializeSpeedSegments()
    {
        _segments = SpeedSegmentEditor.Single(InPoint, OutPoint);
        SelectedSegmentIndex = -1;
        PublishSegments();
    }

    private void ReconcileSegments()
    {
        _segments = SpeedSegmentEditor.Reconcile(_segments, InPoint, OutPoint);
        if (SelectedSegmentIndex >= _segments.Count) SelectedSegmentIndex = -1;
        PublishSegments();
    }

    private void PublishSegments()
    {
        SpeedBands = [.. _segments.Select(s => new TimelineSpeedSegment(s.Start, s.End, s.Speed, s.Removed))];
        OnPropertyChanged(nameof(HasSelectedSegment));
        OnPropertyChanged(nameof(SelectedSpeed));
        OnPropertyChanged(nameof(SelectedSegmentRemoved));
        OnPropertyChanged(nameof(HasSpeedEdits));
        OnPropertyChanged(nameof(SelectedSegmentText));
        OnPropertyChanged(nameof(SpeedSummaryText));
        OnPropertyChanged(nameof(CanExport));
        UpdateProgressBarFraction();
    }

    // Current segments expressed in the trimmed clip's own timeline (0-based, matching an input-seeked
    // export stream), clamped to the selection.
    private List<SpeedSegment> GetRelativeSegments(double selectionLength)
    {
        List<SpeedSegment> reconciled = SpeedSegmentEditor.Reconcile(_segments, InPoint, OutPoint);
        List<SpeedSegment> relative = [];
        foreach (SpeedSegment s in reconciled)
        {
            double start = Math.Clamp(s.Start - InPoint, 0, selectionLength);
            double end = Math.Clamp(s.End - InPoint, 0, selectionLength);
            if (end - start > 1e-3) relative.Add(new SpeedSegment(start, end, s.Speed, s.Removed));
        }
        return relative.Count > 0 ? relative : SpeedSegmentEditor.Single(0, selectionLength);
    }

    [RelayCommand]
    private async Task TogglePlaybackAsync()
    {
        if (IsPlaying)
        {
            StopPlayback();
            await LoadPreviewAsync(PreviewPosition, immediate: true);
        }
        else
        {
            await StartPlaybackAsync();
        }
    }

    private async Task StartPlaybackAsync()
    {
        if (SourceWidth <= 0 || Duration <= 0) return;

        if (!IsPlaybackSequenceValid())
        {
            IsBusy = true;
            StatusText = "Preparing smooth preview...";
            try
            {
                await BuildPlaybackSequenceAsync();
            }
            finally
            {
                IsBusy = false;
            }
            if (!IsPlaybackSequenceValid())
            {
                StatusText = "Could not prepare preview playback.";
                return;
            }
        }

        if (PreviewPosition >= OutPoint - 0.01) PreviewPosition = InPoint;
        double kept = SkipRemoved(PreviewPosition);
        if (kept >= 0) PreviewPosition = kept;

        _playbackClock.Restart();
        _lastTickSeconds = 0;
        _lastShownFrameIndex = -1;
        IsPlaying = true;
        StatusText = "Playing preview.";
        _playbackTimer.Start();
        ShowPlaybackFrame(PreviewPosition);
    }

    private void StopPlayback()
    {
        if (!IsPlaying && !_playbackTimer.IsEnabled) return;
        _playbackTimer.Stop();
        _playbackClock.Stop();
        IsPlaying = false;
    }

    private void OnPlaybackTick(object? sender, EventArgs e)
    {
        // Advance through source time at the local segment speed (times the preview speed control), so the
        // preview plays exactly what the export will produce — sped-up clips race by, slowed clips linger,
        // cut clips are hopped over entirely.
        double now = _playbackClock.Elapsed.TotalSeconds;
        double delta = Math.Max(0, now - _lastTickSeconds);
        _lastTickSeconds = now;

        double basePosition = SkipRemoved(PreviewPosition);
        double rate = SelectedPlaybackSpeed * VideoSpeedGraph.SpeedAt(_segments, basePosition);
        double position = SkipRemoved(basePosition + delta * rate);

        if (position < 0 || position >= OutPoint)
        {
            // Loop within the selection for an easy, continuous preview.
            position = SkipRemoved(InPoint);
            if (position < 0) position = InPoint;
            _playbackClock.Restart();
            _lastTickSeconds = 0;
        }

        _adjustingTime = true;
        PreviewPosition = Math.Clamp(position, InPoint, OutPoint);
        _adjustingTime = false;

        ShowPlaybackFrame(PreviewPosition);
        UpdateCameraRect();
    }

    // The earliest playable time at or after <time>, or -1 when only removed clips remain ahead.
    private double SkipRemoved(double time) =>
        VideoSpeedGraph.HasRemovals(_segments) ? SpeedSegmentEditor.NextKeptTime(_segments, time) : time;

    private void ShowPlaybackFrame(double time)
    {
        if (_playbackFrames == null || _playbackFrames.Length == 0) return;

        int index = (int)Math.Round((time - _playbackSequenceStart) * _playbackSequenceFps);
        index = Math.Clamp(index, 0, _playbackFrames.Length - 1);
        if (index == _lastShownFrameIndex) return;

        _lastShownFrameIndex = index;
        SetPreviewFromBytes(_playbackFrames[index]);
    }

    private async Task BuildPlaybackSequenceAsync()
    {
        _sequenceCancellation?.Cancel();
        _sequenceCancellation?.Dispose();
        _sequenceCancellation = new CancellationTokenSource();

        double start = InPoint;
        double end = OutPoint;
        double span = Math.Max(0.1, end - start);
        double fps = PlaybackFrameRate;
        // Keep the frame count bounded for long selections.
        double maxFrames = 900;
        if (span * fps > maxFrames) fps = Math.Max(4, maxFrames / span);

        try
        {
            VideoEditorPreviewSequence? sequence = await _services.GetPreviewSequence(
                InputFilePath, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), fps, PlaybackMaxWidth, _sequenceCancellation.Token);

            if (sequence == null || sequence.Frames.Count == 0) return;

            _playbackFrames = [.. sequence.Frames];
            _playbackSequenceFps = sequence.Fps;
            _playbackSequenceStart = sequence.Start.TotalSeconds;
            _playbackSequenceEnd = end;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool IsPlaybackSequenceValid()
    {
        return _playbackFrames is { Length: > 0 } &&
            Math.Abs(_playbackSequenceStart - InPoint) < 0.02 &&
            Math.Abs(_playbackSequenceEnd - OutPoint) < 0.02;
    }

    private void InvalidatePlaybackSequence()
    {
        _sequenceCancellation?.Cancel();
        _playbackFrames = null;
        _playbackSequenceEnd = -1;
        _lastShownFrameIndex = -1;
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (!CanExport) return;
        StopPlayback();

        if (string.IsNullOrWhiteSpace(OutputFolderPath) || !Directory.Exists(OutputFolderPath))
        {
            StatusText = "Select an existing output folder.";
            return;
        }
        if (string.IsNullOrWhiteSpace(OutputFileName))
        {
            StatusText = "Enter an output file name.";
            return;
        }
        if (Path.GetFullPath(OutputFilePath).Equals(Path.GetFullPath(InputFilePath), StringComparison.OrdinalIgnoreCase))
        {
            StatusText = "Choose a different output name to keep the original recording safe.";
            return;
        }

        CancelPreview();
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();
        IsBusy = true;
        Progress = 0;
        StatusText = "Exporting edited recording...";
        _options.AutoOpenFolder = AutoOpenFolder;
        IProgress<double> progress = new Progress<double>(value => Progress = Math.Clamp(value, 0, 100));

        try
        {
            (string arguments, IReadOnlyList<string> tempFiles, double outputDuration) = BuildExport();
            VideoEditorExportRequest request = new(arguments, OutputFilePath, TimeSpan.FromSeconds(outputDuration), AutoOpenFolder, tempFiles);
            VideoEditorExportResult result = await _services.Export(request, progress, _operationCancellation.Token);

            if (result.Succeeded && !result.WasCancelled)
            {
                Progress = 100;
                StatusText = $"Edited recording saved: {OutputFilePath}";
            }
            else if (result.WasCancelled)
            {
                StatusText = "Export stopped.";
                Progress = 0;
            }
            else
            {
                StatusText = string.IsNullOrWhiteSpace(result.ErrorMessage) ? "Video export failed." : $"Video export failed: {result.ErrorMessage}";
                Progress = 0;
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Export stopped.";
            Progress = 0;
        }
        catch (Exception ex)
        {
            StatusText = $"Video export failed: {ex.Message}";
            Progress = 0;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Stop()
    {
        if (IsBusy)
        {
            StatusText = "Stopping...";
            _operationCancellation?.Cancel();
        }
    }

    partial void OnInPointChanged(double value)
    {
        if (_adjustingTime) return;
        _adjustingTime = true;
        InPoint = Math.Clamp(value, 0, Math.Max(0, OutPoint - 0.05));
        PreviewPosition = Math.Clamp(PreviewPosition, InPoint, OutPoint);
        _adjustingTime = false;
        StopPlayback();
        InvalidatePlaybackSequence();
        ReconcileSegments();
    }

    partial void OnOutPointChanged(double value)
    {
        if (_adjustingTime) return;
        _adjustingTime = true;
        OutPoint = Math.Clamp(value, Math.Min(Duration, InPoint + 0.05), Duration);
        PreviewPosition = Math.Clamp(PreviewPosition, InPoint, OutPoint);
        _adjustingTime = false;
        StopPlayback();
        InvalidatePlaybackSequence();
        ReconcileSegments();
    }

    partial void OnPreviewPositionChanged(double value)
    {
        if (_adjustingTime)
        {
            UpdateCameraRect();
            return;
        }

        _adjustingTime = true;
        PreviewPosition = Math.Clamp(value, InPoint, OutPoint);
        _adjustingTime = false;

        StopPlayback();
        UpdateCameraRect();
        _ = LoadPreviewAsync(PreviewPosition, immediate: false);
    }

    partial void OnCropRectChanged(Rect value)
    {
        if (!_applyingCropPreset && SelectedCropAspect != CropAspects[0])
        {
            SelectedCropAspect = CropAspects[0];
        }

        UpdateCameraRect();
    }

    partial void OnSelectedCropAspectChanged(VideoCropAspectItem value)
    {
        if (!_applyingCropPreset && (value.IsOriginal || value.Ratio != null) && SourceWidth > 0 && SourceHeight > 0)
        {
            ApplyCropPreset(value);
        }
    }

    partial void OnAutoOpenFolderChanged(bool value) => _options.AutoOpenFolder = value;

    partial void OnIdleThresholdSecondsChanged(double value) => _options.IdleThresholdSeconds = value;

    partial void OnEffectClickRipplesChanged(bool value)
    {
        _options.EffectClickRipples = value;
        OnPropertyChanged(nameof(RipplePreviewActive));
    }

    partial void OnPhoneTapModeChanged(bool value)
    {
        _options.PhoneTapMode = value;
        OnPropertyChanged(nameof(PhoneTapActive));
        OnPropertyChanged(nameof(RipplePreviewActive));
        RefreshCursorSprite();
    }

    partial void OnPhoneTapSizeChanged(double value) => _options.PhoneTapSize = value;

    partial void OnPhoneTapBrightnessChanged(double value) => _options.PhoneTapBrightness = value;

    partial void OnPhoneTapImpactChanged(double value) => _options.PhoneTapImpact = value;

    partial void OnEffectSpotlightChanged(bool value)
    {
        _options.EffectSpotlight = value;
        OnPropertyChanged(nameof(SpotlightPreviewActive));
        UpdateSpotlightCenter();
    }

    partial void OnSpotlightSizeChanged(double value) => _options.SpotlightSize = value;

    partial void OnEffectStudioBackgroundChanged(bool value)
    {
        _options.EffectStudioBackground = value;
        if (value) StatusText = "Studio background on — the preview and the export are framed on the backdrop.";
    }

    partial void OnSelectedStudioStyleChanged(VideoStudioStyleItem value)
    {
        _options.StudioStyle = value.DisplayName;
        OnPropertyChanged(nameof(StudioTopColor));
        OnPropertyChanged(nameof(StudioBottomColor));
    }

    partial void OnEffectProgressBarChanged(bool value)
    {
        _options.EffectProgressBar = value;
        UpdateProgressBarFraction();
    }

    partial void OnSmoothCursorEnabledChanged(bool value)
    {
        _options.SmoothCursor = value;
        RefreshCursorSprite();
    }

    partial void OnSelectedCursorStyleChanged(VideoCursorStyleItem value)
    {
        if (value is null) return;
        _options.CursorStyle = value.Id;
        RefreshCursorSprite();
    }

    partial void OnCursorSizeChanged(double value)
    {
        _options.CursorSize = value;
        RefreshCursorSprite();
    }

    partial void OnCursorSmoothingChanged(double value)
    {
        _options.CursorSmoothing = value;
        RebuildCursorPath();
    }

    partial void OnSelectedExportSizeChanged(VideoExportSizeItem value) => _options.ExportMaxHeight = value.MaxHeight;

    partial void OnSelectedExportQualityChanged(VideoExportQualityItem value) => _options.ExportCrf = value.Crf;

    partial void OnAutoZoomEnabledChanged(bool value)
    {
        IsCropEditable = !value;
        RebuildAutoZoomPlan();
    }

    partial void OnAutoZoomAmountChanged(double value)
    {
        _options.AutoZoomAmount = value;
        RebuildAutoZoomPlan();
    }

    partial void OnAutoZoomSmoothnessChanged(double value)
    {
        _options.AutoZoomSmoothness = value;
        RebuildAutoZoomPlan();
    }

    private void RebuildAutoZoomPlan()
    {
        if (HasMotionData && AutoZoomEnabled && _motionData != null)
        {
            AutoZoomParameters parameters = new(true, AutoZoomAmount, AutoZoomSmoothness);
            _autoZoomPlan = AutoZoomPlanner.Build(_motionData, parameters, FramesPerSecond > 0 ? Math.Min(FramesPerSecond, 30) : 30);

            ZoomRegions = [.. _autoZoomPlan.ZoomRegions.Select(r => new TimelineZoomRegion(r.Start, r.End))];
            int clicks = _motionData.Clicks?.Count ?? 0;
            AutoZoomStatusText = _autoZoomPlan.HasZoom
                ? $"{_autoZoomPlan.ZoomRegions.Count} zoom moment(s) • {clicks} click(s) • up to {_autoZoomPlan.MaxZoom:0.0}×"
                : "No strong focus points detected — try increasing the amount.";
        }
        else
        {
            _autoZoomPlan = AutoZoomPlan.Empty;
            ZoomRegions = [];
            AutoZoomStatusText = HasMotionData ? "" : "No motion track was recorded with this video.";
        }

        UpdateCameraRect();
    }

    private void UpdateCameraRect()
    {
        if (AutoZoomEnabled && _autoZoomPlan.HasZoom && SourceWidth > 0 && SourceHeight > 0)
        {
            AutoZoomState state = _autoZoomPlan.Sample(PreviewPosition);
            Rect crop = CropRect.Width > 0 && CropRect.Height > 0 ? CropRect : new Rect(0, 0, SourceWidth, SourceHeight);
            CameraRect = AutoZoomFilter.ComputeCameraRect(SourceWidth, SourceHeight, crop, state);
        }
        else if (CameraRect != default)
        {
            CameraRect = default;
        }

        UpdateSpotlightCenter();
        UpdateCursorPosition();
        UpdateProgressBarFraction();
    }

    private void UpdateSpotlightCenter()
    {
        if (EffectSpotlight && _motionData is { HasSamples: true })
        {
            (double x, double y) = VideoEffectsGraph.SampleCursor(_motionData.Samples, PreviewPosition);
            SpotlightCenter = new Point(x, y);
        }
    }

    private void UpdateProgressBarFraction()
    {
        if (!EffectProgressBar) return;
        double total = VideoSpeedGraph.OutputDuration(SpeedSegmentEditor.Reconcile(_segments, InPoint, OutPoint));
        ProgressBarFraction = total > 0
            ? Math.Clamp(VideoSpeedGraph.OutputTimeAt(_segments, PreviewPosition) / total, 0, 1)
            : 0;
    }

    private void ApplyCropPreset(VideoCropAspectItem aspect)
    {
        _applyingCropPreset = true;
        SelectedCropAspect = aspect;

        if (aspect.IsOriginal)
        {
            CropRect = new Rect(0, 0, SourceWidth, SourceHeight);
        }
        else if (aspect.Ratio != null)
        {
            double targetRatio = aspect.Ratio.Value;
            double width = SourceWidth;
            double height = width / targetRatio;
            if (height > SourceHeight)
            {
                height = SourceHeight;
                width = height * targetRatio;
            }
            CropRect = new Rect(Math.Round((SourceWidth - width) / 2), Math.Round((SourceHeight - height) / 2), Math.Round(width), Math.Round(height));
        }

        _applyingCropPreset = false;
    }

    private void SetCrop(double x, double y, double width, double height)
    {
        width = Math.Clamp(width, 2, Math.Max(2, SourceWidth));
        height = Math.Clamp(height, 2, Math.Max(2, SourceHeight));
        x = Math.Clamp(x, 0, Math.Max(0, SourceWidth - width));
        y = Math.Clamp(y, 0, Math.Max(0, SourceHeight - height));
        CropRect = new Rect(Math.Round(x), Math.Round(y), Math.Round(width), Math.Round(height));
    }

    private async Task LoadPreviewAsync(double seconds, bool immediate)
    {
        if (_disposed || string.IsNullOrWhiteSpace(InputFilePath)) return;
        CancelPreview();
        CancellationTokenSource cancellation = new();
        _previewCancellation = cancellation;

        try
        {
            if (!immediate) await Task.Delay(120, cancellation.Token);
            byte[]? bytes = await _services.GetPreview(InputFilePath, TimeSpan.FromSeconds(seconds), cancellation.Token);
            if (bytes == null || bytes.Length == 0 || cancellation.IsCancellationRequested) return;
            SetPreviewFromBytes(bytes);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cancellation.IsCancellationRequested) StatusText = $"Could not update preview: {ex.Message}";
        }
    }

    private void SetPreviewFromBytes(byte[] bytes)
    {
        try
        {
            Bitmap bitmap = new(new MemoryStream(bytes, writable: false));
            Bitmap? previous = PreviewImage;
            PreviewImage = bitmap;
            previous?.Dispose();
        }
        catch
        {
        }
    }

    private void CancelPreview()
    {
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = null;
    }

    private (string Arguments, IReadOnlyList<string> TempFiles, double OutputDuration) BuildExport()
    {
        Rect crop = GetEvenCrop();
        double length = Math.Max(0.05, OutPoint - InPoint);
        string start = InPoint.ToString("0.###", CultureInfo.InvariantCulture);
        string duration = length.ToString("0.###", CultureInfo.InvariantCulture);
        bool cropNeeded = crop.X != 0 || crop.Y != 0 || crop.Width != SourceWidth || crop.Height != SourceHeight;
        bool zoomNeeded = AutoZoomEnabled && _autoZoomPlan.HasZoom;
        double fps = FramesPerSecond > 0 ? FramesPerSecond : 30;

        List<SpeedSegment> segments = GetRelativeSegments(length);
        bool speedNeeded = VideoSpeedGraph.HasEdits(segments);
        double outputDuration = speedNeeded ? VideoSpeedGraph.OutputDuration(segments) : length;

        string encode = $"-c:v libx264 -preset veryfast -crf {SelectedExportQuality.Crf} -pix_fmt yuv420p -movflags +faststart";

        // The export is one linear pipeline; stages that are off simply don't emit filters.
        // Order matters: source-space effects (ripples) ride along with auto-zoom, speed warps last so
        // every earlier stage stays on the source clock, then output-space stages (scale) run.
        ExportGraphBuilder graph = new();
        string current = "[0:v]";

        if (cropNeeded)
        {
            current = graph.Chain(current, $"crop={(int)crop.Width}:{(int)crop.Height}:{(int)crop.X}:{(int)crop.Y}");
        }

        if (EffectSpotlight && _motionData is { HasSamples: true })
        {
            List<(double T, double X, double Y)> cursorPath = VideoEffectsGraph.DecimateCursorPath(_motionData.Samples, InPoint, length);
            current = VideoEffectsGraph.AddSpotlight(graph, current, cursorPath, SourceWidth, SourceHeight, crop, SpotlightSize, length, fps);
        }

        // Ripples draw after the spotlight so click pulses stay bright inside the dimmed area.
        if ((EffectClickRipples || PhoneTapActive) && _motionData?.Clicks is { Count: > 0 })
        {
            List<VideoClickHighlight> clicks = VideoEffectsGraph.ResolveClicks(_motionData.Clicks, InPoint, length);
            current = VideoEffectsGraph.AddClickRipples(graph, current, clicks, SourceWidth, SourceHeight, crop,
                PhoneTapActive, PhoneTapSize, PhoneTapBrightness, PhoneTapImpact);
        }

        // The cursor goes on top of the other source-space effects and is magnified by auto-zoom along
        // with the content, exactly like a real one would be.
        if (_cursorPath != null && _cursorSprite != null)
        {
            current = VideoCursorGraph.AddCursor(graph, current, _cursorPath, _cursorSprite, InPoint, length, SourceWidth, SourceHeight, crop);
        }

        if (zoomNeeded)
        {
            // The export seeks to InPoint (-ss), so zoompan's clock (on/fps) restarts at 0 there. Shift the
            // plan's absolute keyframe times into the trimmed clip's timeline.
            IReadOnlyList<AutoZoomKeyframe> keyframes = [.. _autoZoomPlan.GetKeyframes().Select(k => k with { Time = k.Time - InPoint })];
            string zoomOut = graph.NewLabel();
            graph.Parts.Add(AutoZoomFilter.BuildZoomPanFilter(
                keyframes, SourceWidth, SourceHeight, crop, (int)crop.Width, (int)crop.Height, fps, current, zoomOut));
            current = zoomOut;
        }

        if (speedNeeded)
        {
            // zoompan re-times its output to a constant fps, so the speed pass must run after it.
            string speedOut = graph.NewLabel();
            graph.Parts.Add(VideoSpeedGraph.Build(segments, false, 0, 0, 0, 0, _hasAudio, speedOut, current));
            current = speedOut;
        }

        // Optional downscale cap. Dimensions are computed here as exact even constants because later
        // stages (studio background) need to know the true frame size.
        int frameWidth = (int)crop.Width;
        int frameHeight = (int)crop.Height;
        int maxHeight = SelectedExportSize.MaxHeight;
        if (maxHeight > 0 && frameHeight > maxHeight)
        {
            frameWidth = Math.Max(2, (int)Math.Round(frameWidth * (maxHeight / (double)frameHeight)) / 2 * 2);
            frameHeight = maxHeight;
            current = graph.Chain(current, $"scale={frameWidth}:{frameHeight}");
        }

        if (EffectStudioBackground)
        {
            current = VideoEffectsGraph.AddStudioBackground(
                graph, current, frameWidth, frameHeight, SelectedStudioStyle.Top, SelectedStudioStyle.Bottom);
        }

        if (EffectProgressBar)
        {
            current = VideoEffectsGraph.AddProgressBar(graph, current, frameWidth, frameHeight, outputDuration);
        }

        if (!graph.HasFilters)
        {
            string plain =
                $"-ss {start} -i \"{InputFilePath}\" -t {duration} -map 0:v:0 -map 0:a? " +
                $"{encode} -c:a aac -b:a 128k -y \"{OutputFilePath}\"";
            return (plain, [], length);
        }

        string graphFile = WriteGraphFile(graph.BuildGraph(current));
        List<string> tempFiles = [.. graph.TempFiles, graphFile];

        string audio = speedNeeded
            ? (_hasAudio ? "-map \"[a]\" -c:a aac -b:a 128k" : "-an")
            : "-map 0:a? -c:a aac -b:a 128k";
        string inputs = string.Join(" ", graph.ExtraInputs);
        if (inputs.Length > 0) inputs += " ";

        string arguments =
            $"-ss {start} -t {duration} -i \"{InputFilePath}\" {inputs}-filter_complex_script \"{graphFile}\" " +
            $"-map \"[v]\" {audio} {encode} -y \"{OutputFilePath}\"";

        return (arguments, tempFiles, outputDuration);
    }

    private static string WriteGraphFile(string graph)
    {
        string graphFile = Path.Combine(Path.GetTempPath(), $"ShareX-videoedit-{Guid.NewGuid():N}.txt");
        File.WriteAllText(graphFile, graph);
        return graphFile;
    }

    private Rect GetEvenCrop()
    {
        int x = Math.Max(0, (int)Math.Floor(CropRect.X / 2) * 2);
        int y = Math.Max(0, (int)Math.Floor(CropRect.Y / 2) * 2);
        int width = Math.Max(2, (int)Math.Floor(Math.Min(CropRect.Width, SourceWidth - x) / 2) * 2);
        int height = Math.Max(2, (int)Math.Floor(Math.Min(CropRect.Height, SourceHeight - y) / 2) * 2);
        return new Rect(x, y, width, height);
    }

    private double GetFrameStep() => FramesPerSecond > 0 ? 1d / FramesPerSecond : 0.1;

    private static string FormatTime(double seconds)
    {
        TimeSpan value = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss\.fff") : value.ToString(@"m\:ss\.fff");
    }

    private static string FormatSpeed(double speed) =>
        (speed % 1 == 0 ? speed.ToString("0") : speed.ToString("0.##", CultureInfo.InvariantCulture)) + "×";

    private static bool TryParseTime(string? text, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (TimeSpan.TryParse(text, CultureInfo.CurrentCulture, out TimeSpan time))
        {
            seconds = time.TotalSeconds;
            return true;
        }
        return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out seconds);
    }

    public void Dispose()
    {
        _disposed = true;
        StopPlayback();
        CancelPreview();
        _sequenceCancellation?.Cancel();
        _sequenceCancellation?.Dispose();
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        PreviewImage?.Dispose();
        CursorImage?.Dispose();
    }
}
