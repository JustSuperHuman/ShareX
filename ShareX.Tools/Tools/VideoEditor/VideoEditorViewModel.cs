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
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Globalization;

namespace ShareX.Tools;

public sealed record VideoCropAspectItem(string DisplayName, double? Ratio, bool IsOriginal = false);

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

    private readonly VideoEditorOptions _options;
    private readonly VideoEditorServices _services;
    private CancellationTokenSource? _operationCancellation;
    private CancellationTokenSource? _previewCancellation;
    private bool _adjustingTime;
    private bool _applyingCropPreset;
    private bool _disposed;

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

    public VideoEditorViewModel(VideoEditorOptions options, VideoEditorServices services, string? inputFilePath = null)
    {
        _options = options;
        _services = services;
        _autoOpenFolder = options.AutoOpenFolder;
        InputFilePath = inputFilePath ?? string.Empty;
    }

    public Func<string, Task<string?>>? SelectInputFileRequested { get; set; }
    public Func<string, Task<string?>>? SelectOutputFolderRequested { get; set; }

    public bool IsIdle => !IsBusy;
    public bool CanExport => IsIdle && Duration > 0 && OutPoint > InPoint && SourceWidth > 0 && SourceHeight > 0;
    public bool HasNoPreview => PreviewImage == null;
    public string InputFileDisplay => string.IsNullOrWhiteSpace(InputFilePath) ? "No file selected" : InputFilePath;
    public string SourceInfoText => SourceWidth <= 0 ? string.Empty : $"{SourceWidth} × {SourceHeight}  •  {FramesPerSecond:0.##} fps";
    public string DurationText => FormatTime(Duration);
    public string PreviewTimeText => FormatTime(PreviewPosition);
    public string SelectionDurationText => FormatTime(Math.Max(0, OutPoint - InPoint));
    public string CropSizeText => CropWidth > 0 && CropHeight > 0 ? $"{CropWidth:0} × {CropHeight:0}" : string.Empty;

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

        CancelPreview();
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
            Duration = info.Duration.TotalSeconds;
            InPoint = 0;
            OutPoint = Duration;
            PreviewPosition = 0;
            ApplyCropPreset(CropAspects[1]);
            OutputFolderPath = Path.GetDirectoryName(filePath) ?? string.Empty;
            OutputFileName = $"{Path.GetFileNameWithoutExtension(filePath)}-edited.mp4";
            StatusText = "Drag the timeline handles to trim, and drag the frame handles to crop.";
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
    private void StepBackward() => PreviewPosition = Math.Max(InPoint, PreviewPosition - GetFrameStep());

    [RelayCommand]
    private void StepForward() => PreviewPosition = Math.Min(OutPoint, PreviewPosition + GetFrameStep());

    [RelayCommand]
    private void ResetCrop() => ApplyCropPreset(CropAspects[1]);

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (!CanExport) return;
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
            TimeSpan selectionDuration = TimeSpan.FromSeconds(OutPoint - InPoint);
            VideoEditorExportRequest request = new(BuildExportArguments(), OutputFilePath, selectionDuration, AutoOpenFolder);
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
    }

    partial void OnOutPointChanged(double value)
    {
        if (_adjustingTime) return;
        _adjustingTime = true;
        OutPoint = Math.Clamp(value, Math.Min(Duration, InPoint + 0.05), Duration);
        PreviewPosition = Math.Clamp(PreviewPosition, InPoint, OutPoint);
        _adjustingTime = false;
    }

    partial void OnPreviewPositionChanged(double value)
    {
        if (_adjustingTime) return;
        _adjustingTime = true;
        PreviewPosition = Math.Clamp(value, InPoint, OutPoint);
        _adjustingTime = false;
        _ = LoadPreviewAsync(PreviewPosition, immediate: false);
    }

    partial void OnCropRectChanged(Rect value)
    {
        if (!_applyingCropPreset && SelectedCropAspect != CropAspects[0])
        {
            SelectedCropAspect = CropAspects[0];
        }
    }

    partial void OnSelectedCropAspectChanged(VideoCropAspectItem value)
    {
        if (!_applyingCropPreset && (value.IsOriginal || value.Ratio != null) && SourceWidth > 0 && SourceHeight > 0)
        {
            ApplyCropPreset(value);
        }
    }

    partial void OnAutoOpenFolderChanged(bool value) => _options.AutoOpenFolder = value;

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
            Bitmap bitmap = new(new MemoryStream(bytes, writable: false));
            Bitmap? previous = PreviewImage;
            PreviewImage = bitmap;
            previous?.Dispose();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cancellation.IsCancellationRequested) StatusText = $"Could not update preview: {ex.Message}";
        }
    }

    private void CancelPreview()
    {
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = null;
    }

    private string BuildExportArguments()
    {
        Rect crop = GetEvenCrop();
        double length = Math.Max(0.05, OutPoint - InPoint);
        string start = InPoint.ToString("0.###", CultureInfo.InvariantCulture);
        string duration = length.ToString("0.###", CultureInfo.InvariantCulture);
        string filter = crop.X == 0 && crop.Y == 0 && crop.Width == SourceWidth && crop.Height == SourceHeight
            ? string.Empty
            : $"-vf \"crop={(int)crop.Width}:{(int)crop.Height}:{(int)crop.X}:{(int)crop.Y}\" ";

        return $"-ss {start} -i \"{InputFilePath}\" -t {duration} -map 0:v:0 -map 0:a? {filter}" +
            "-c:v libx264 -preset veryfast -crf 18 -pix_fmt yuv420p -c:a aac -b:a 128k -movflags +faststart " +
            $"-y \"{OutputFilePath}\"";
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
        CancelPreview();
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        PreviewImage?.Dispose();
    }
}
