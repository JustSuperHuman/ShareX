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

using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Drawing;

namespace ShareX.Tools;

public sealed partial class RecordingStudioViewModel : ViewModelBase, IDisposable
{
    private static readonly RecordingDeviceItem NoCamera = new("", "No camera", "Record the screen on its own");
    private static readonly RecordingDeviceItem NoMicrophone = new("", "No microphone", "Record without your voice");

    private readonly RecordingStudioOptions _options;
    private readonly RecordingStudioServices _services;
    private readonly Dictionary<string, CameraFormat?> _cameraFormats = [];
    private IReadOnlyList<RecordingWindowInfo> _windowsFrontToBack = [];
    private MicrophoneLevelFeed? _microphoneFeed;
    private bool _loading = true;
    private bool _started;
    private bool _disposed;

    public ObservableCollection<RecordingDisplayItem> Displays { get; } = [];
    public ObservableCollection<RecordingWindowItem> Windows { get; } = [];
    public ObservableCollection<RecordingDeviceItem> Cameras { get; } = [NoCamera];
    public ObservableCollection<RecordingDeviceItem> Microphones { get; } = [NoMicrophone];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDisplay))]
    [NotifyPropertyChangedFor(nameof(IsWindow))]
    [NotifyPropertyChangedFor(nameof(IsArea))]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    private RecordingTargetKind _targetKind;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    private RecordingDisplayItem? _selectedDisplay;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    private RecordingWindowItem? _selectedWindow;

    [ObservableProperty]
    private RecordingDeviceItem _selectedCamera = NoCamera;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MicrophoneActive))]
    private RecordingDeviceItem _selectedMicrophone = NoMicrophone;

    [ObservableProperty]
    private double _microphoneLevel;

    [ObservableProperty]
    private bool _systemAudio;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SystemAudioMissing))]
    private bool _systemAudioAvailable = true;

    [ObservableProperty]
    private bool _uploadToCap;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoWindows))]
    private bool _devicesLoaded;

    public RecordingStudioViewModel(RecordingStudioOptions options, RecordingStudioServices services)
    {
        _options = options;
        _services = services;
        _targetKind = options.TargetKind;
        _uploadToCap = options.UploadToCap && services.CapConfigured;
        CameraBubbleWindow.ClosedByUser += OnCameraClosedByUser;
    }

    public Action? CloseRequested { get; set; }

    // Asks the window to let the user point at a display or window on screen.
    public Action<RecordingTargetKind>? PickRequested { get; set; }

    public bool IsDisplay => TargetKind == RecordingTargetKind.Display;
    public bool IsWindow => TargetKind == RecordingTargetKind.Window;
    public bool IsArea => TargetKind == RecordingTargetKind.Area;
    public bool MicrophoneActive => !SelectedMicrophone.IsNone;
    public bool SystemAudioMissing => !SystemAudioAvailable;
    public bool CapConfigured => _services.CapConfigured;
    public bool CapNotConfigured => !_services.CapConfigured;
    public bool HasNoWindows => DevicesLoaded && Windows.Count == 0;

    public bool CanStart => TargetKind switch
    {
        RecordingTargetKind.Display => SelectedDisplay != null,
        RecordingTargetKind.Window => SelectedWindow != null,
        _ => true
    };

    public async Task InitializeAsync()
    {
        foreach (RecordingDisplayItem display in RecordingDevices.ListDisplays()) Displays.Add(display);
        SelectedDisplay = Displays.FirstOrDefault(d => d.Primary) ?? Displays.FirstOrDefault();
        RefreshWindows();

        string ffmpegPath = _services.FFmpegPath;
        (List<RecordingDevices.Device> cameras, List<RecordingDevices.Device> microphones, bool systemAudio) =
            await Task.Run(() => RecordingDevices.ListDevices(ffmpegPath));
        if (_disposed) return;

        // Ask each device what it can do off the UI thread; ffmpeg takes a moment per device.
        List<(RecordingDevices.Device Device, CameraFormat? Format)> cameraDetails = await Task.Run(() =>
            cameras.Select(c => (c, RecordingDevices.GetCameraFormat(ffmpegPath, c.Id))).ToList());
        List<(RecordingDevices.Device Device, MicrophoneFormat? Format)> microphoneDetails = await Task.Run(() =>
            microphones.Select(m => (m, RecordingDevices.GetMicrophoneFormat(ffmpegPath, m.Id))).ToList());
        if (_disposed) return;

        foreach ((RecordingDevices.Device device, CameraFormat? format) in cameraDetails)
        {
            _cameraFormats[device.Id] = format;
            Cameras.Add(new RecordingDeviceItem(device.Id, device.Name, format?.ToString() ?? "Camera"));
        }

        foreach ((RecordingDevices.Device device, MicrophoneFormat? format) in microphoneDetails)
        {
            Microphones.Add(new RecordingDeviceItem(device.Id, device.Name, format?.ToString() ?? "Microphone"));
        }

        SystemAudioAvailable = systemAudio;
        SystemAudio = _options.SystemAudio && systemAudio;

        _loading = false;
        DevicesLoaded = true;

        // A remembered device that has been unplugged quietly falls back to "none".
        SelectedMicrophone = Microphones.FirstOrDefault(m => m.Device == _options.MicrophoneDevice) ?? NoMicrophone;
        SelectedCamera = Cameras.FirstOrDefault(c => c.Device == _options.CameraDevice) ?? NoCamera;
    }

    [RelayCommand]
    private void SelectTarget(string? kind)
    {
        if (!Enum.TryParse(kind, out RecordingTargetKind parsed)) return;

        TargetKind = parsed;
    }

    [RelayCommand]
    private void PickOnScreen() => PickRequested?.Invoke(TargetKind);

    public IReadOnlyList<TargetPickerItem> GetPickerItems(RecordingTargetKind kind)
    {
        if (kind == RecordingTargetKind.Display)
        {
            return [.. Displays.Select(d => new TargetPickerItem(d.Bounds, d.Title, d.Detail, null, d))];
        }

        RefreshWindows();
        return [.. _windowsFrontToBack.Select(w => new TargetPickerItem(
            w.Bounds,
            string.IsNullOrWhiteSpace(w.AppName) ? w.Title : w.AppName,
            $"{w.Bounds.Width} × {w.Bounds.Height}",
            w.Icon,
            w))];
    }

    public void ApplyPick(TargetPickerItem? item)
    {
        if (item?.Value is RecordingDisplayItem display)
        {
            SelectedDisplay = display;
        }
        else if (item?.Value is RecordingWindowInfo window)
        {
            SelectedWindow = Windows.FirstOrDefault(w => w.Info.Handle == window.Handle) ?? SelectedWindow;
        }
    }

    [RelayCommand]
    private void RefreshWindows()
    {
        IntPtr previous = SelectedWindow?.Info.Handle ?? IntPtr.Zero;
        Windows.Clear();

        _windowsFrontToBack = _services.GetWindows();
        foreach (RecordingWindowInfo window in _windowsFrontToBack
            .OrderBy(w => w.AppName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(w => w.Title, StringComparer.OrdinalIgnoreCase))
        {
            Windows.Add(new RecordingWindowItem(window));
        }

        SelectedWindow = Windows.FirstOrDefault(w => w.Info.Handle == previous) ?? Windows.FirstOrDefault();
        OnPropertyChanged(nameof(HasNoWindows));
    }

    [RelayCommand]
    private void OpenCapSettings() => _services.OpenCapSettings();

    [RelayCommand]
    private void OpenAudioDeviceDownload() => _services.OpenAudioDeviceDownload();

    [RelayCommand]
    private void Start()
    {
        if (!CanStart) return;

        // The recorder opens the microphone itself, so the meter has to let go of it first.
        StopMicrophoneFeed();

        List<string> audioDevices = [];
        if (!SelectedMicrophone.IsNone) audioDevices.Add(SelectedMicrophone.Device);
        if (SystemAudio && SystemAudioAvailable) audioDevices.Add(RecordingDevices.SystemAudioDevice);

        Rectangle region = TargetKind switch
        {
            RecordingTargetKind.Display => SelectedDisplay!.Bounds,
            RecordingTargetKind.Window => SelectedWindow!.Info.Bounds,
            _ => Rectangle.Empty
        };
        IntPtr handle = TargetKind == RecordingTargetKind.Window ? SelectedWindow!.Info.Handle : IntPtr.Zero;

        _started = true;
        RecordingStudioRequest request = new(TargetKind, region, handle, audioDevices, UploadToCap && CapConfigured);
        CloseRequested?.Invoke();
        _services.Start(request);
    }

    partial void OnTargetKindChanged(RecordingTargetKind value)
    {
        _options.TargetKind = value;
        if (value == RecordingTargetKind.Window) RefreshWindows();
    }

    partial void OnSelectedCameraChanged(RecordingDeviceItem value)
    {
        if (_loading || value == null) return;
        _options.CameraDevice = value.Device;

        if (value.IsNone)
        {
            CameraBubbleWindow.CloseCamera();
        }
        else
        {
            _cameraFormats.TryGetValue(value.Device, out CameraFormat? format);
            CameraBubbleWindow.ShowCamera(_services.FFmpegPath, value.Device, format, _options);
        }
    }

    partial void OnSelectedMicrophoneChanged(RecordingDeviceItem value)
    {
        if (_loading || value == null) return;
        _options.MicrophoneDevice = value.Device;

        StopMicrophoneFeed();
        if (!value.IsNone)
        {
            _microphoneFeed = new MicrophoneLevelFeed(_services.FFmpegPath, value.Device);
            _microphoneFeed.LevelChanged += level => Dispatcher.UIThread.Post(() =>
            {
                // Rise instantly, fall gently, so the meter reads as speech rather than flicker.
                MicrophoneLevel = level > MicrophoneLevel ? level : MicrophoneLevel * 0.8 + level * 0.2;
            });
        }
    }

    partial void OnSystemAudioChanged(bool value)
    {
        if (!_loading) _options.SystemAudio = value;
    }

    partial void OnUploadToCapChanged(bool value) => _options.UploadToCap = value;

    private void OnCameraClosedByUser()
    {
        _loading = true;
        SelectedCamera = NoCamera;
        _loading = false;
        _options.CameraDevice = "";
    }

    private void StopMicrophoneFeed()
    {
        _microphoneFeed?.Dispose();
        _microphoneFeed = null;
        MicrophoneLevel = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CameraBubbleWindow.ClosedByUser -= OnCameraClosedByUser;
        StopMicrophoneFeed();

        // The camera stays up while a recording that was started from here is running.
        if (!_started) CameraBubbleWindow.CloseCamera();
    }
}
