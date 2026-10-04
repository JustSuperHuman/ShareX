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

using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using ShareX.AvaloniaUI.Theming;

namespace ShareX.Tools;

public partial class VideoEditorWindow : Window
{
    private readonly VideoEditorViewModel _viewModel;

    public VideoEditorWindow()
        : this(
            new VideoEditorOptions(),
            new VideoEditorServices(
                (_, _) => Task.FromResult(new VideoEditorMediaInfo(TimeSpan.Zero, 0, 0, 0, false, "FFmpeg is unavailable.")),
                (_, _, _) => Task.FromResult<byte[]?>(null),
                (_, _, _, _, _, _) => Task.FromResult<VideoEditorPreviewSequence?>(null),
                (_, _, _) => Task.FromResult(new VideoEditorExportResult(false, false, "FFmpeg is unavailable."))))
    {
    }

    public VideoEditorWindow(VideoEditorOptions options, VideoEditorServices services, string? inputFilePath = null)
    {
        _viewModel = new VideoEditorViewModel(options, services, inputFilePath);
        DataContext = _viewModel;
        InitializeComponent();
        RequestedThemeVariant = ThemeManager.GetCurrentTheme();
        _viewModel.SelectInputFileRequested = SelectInputFileAsync;
        _viewModel.SelectOutputFolderRequested = SelectOutputFolderAsync;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);
        Opened += async (_, _) => await _viewModel.InitializeAsync();
        Closed += (_, _) => _viewModel.Dispose();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // Editor-wide shortcuts, tunneled so they win over focused buttons. Anything that owns text or
    // list navigation (text boxes, combo boxes, sliders) keeps its keys.
    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (FocusManager?.GetFocusedElement() is TextBox or ComboBox or ComboBoxItem or RangeBase) return;

        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        switch (e.Key)
        {
            case Key.Space:
                _viewModel.TogglePlaybackCommand.Execute(null);
                break;
            case Key.Left when shift:
                _viewModel.JumpBySeconds(-1);
                break;
            case Key.Right when shift:
                _viewModel.JumpBySeconds(1);
                break;
            case Key.Left:
                _viewModel.StepBackwardCommand.Execute(null);
                break;
            case Key.Right:
                _viewModel.StepForwardCommand.Execute(null);
                break;
            case Key.Home:
                _viewModel.GoToStartCommand.Execute(null);
                break;
            case Key.End:
                _viewModel.GoToEndCommand.Execute(null);
                break;
            case Key.I:
                _viewModel.SetStartCommand.Execute(null);
                break;
            case Key.O:
                _viewModel.SetEndCommand.Execute(null);
                break;
            case Key.S when !ctrl:
                _viewModel.SplitAtPlayheadCommand.Execute(null);
                break;
            case Key.Delete:
            case Key.Back:
                _viewModel.ToggleRemoveSegmentCommand.Execute(null);
                break;
            case Key.Z when ctrl:
                _viewModel.UndoSegmentEditCommand.Execute(null);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private async Task<string?> SelectInputFileAsync(string title)
    {
        IStorageFolder? start = await GetSuggestedFolderAsync(Path.GetDirectoryName(_viewModel.InputFilePath));
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = start,
            FileTypeFilter =
            [
                new FilePickerFileType("Video files")
                {
                    Patterns = ["*.mp4", "*.mkv", "*.webm", "*.avi", "*.mov", "*.m4v", "*.wmv"]
                },
                FilePickerFileTypes.All
            ]
        });
        return files.Count > 0 ? files[0].Path.LocalPath : null;
    }

    private async Task<string?> SelectOutputFolderAsync(string title)
    {
        IStorageFolder? start = await GetSuggestedFolderAsync(_viewModel.OutputFolderPath);
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = start
        });
        return folders.Count > 0 ? folders[0].Path.LocalPath : null;
    }

    private async Task<IStorageFolder?> GetSuggestedFolderAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return null;
        return await StorageProvider.TryGetFolderFromPathAsync(new Uri(path));
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = _viewModel.IsIdle && e.DataTransfer.Formats.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (!_viewModel.IsIdle) return;
        IStorageFile? file = e.DataTransfer.TryGetFiles()?.OfType<IStorageFile>().FirstOrDefault();
        if (file != null)
        {
            e.Handled = true;
            await _viewModel.LoadInputAsync(file.Path.LocalPath);
        }
    }
}
