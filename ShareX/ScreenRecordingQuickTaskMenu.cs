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

#nullable enable

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ShareX.AvaloniaUI.Theming;
using ShareX.HelpersLib;
using ShareX.Localization;
using System;

namespace ShareX;

public enum ScreenRecordingQuickTaskAction
{
    Continue,
    EditVideo,
    CopyFilePath,
    CopyFile,
    EditWithCapCut
}

// The menu shown at the cursor when a screen recording finishes. Escape continues with the normal
// after-capture tasks, so a recording is never left hanging.
public sealed class ScreenRecordingQuickTaskMenu
{
    public event Action<ScreenRecordingQuickTaskAction, QuickTaskInfo?>? ActionSelected;

    private bool _actionSelected;
    private ContextMenu? _menu;
    private Window? _placementWindow;

    public void ShowMenu(string filePath)
    {
        Dispatcher.UIThread.Post(() => ShowMenuCore(filePath));
    }

    private void ShowMenuCore(string filePath)
    {
        ContextMenu menu = new();
        MenuItem continueItem = CreateActionItem(Strings.QuickTaskMenu_ShowMenu_Continue,
            LucideIcons.circle_play, ScreenRecordingQuickTaskAction.Continue);
        menu.Items.Add(continueItem);

        bool addedPreset = false;
        if (ApplicationState.SettingsOrNull?.QuickTaskPresets is { Count: > 0 } presets)
        {
            foreach (QuickTaskInfo preset in presets)
            {
                if (IsVideoPreset(preset))
                {
                    if (!addedPreset) menu.Items.Add(new Separator());
                    menu.Items.Add(CreatePresetItem(preset));
                    addedPreset = true;
                }
            }
        }
        menu.Items.Add(new Separator());

        if (FileHelpers.IsVideoFile(filePath))
        {
            menu.Items.Add(CreateActionItem(Strings.ScreenRecordingQuickTaskMenu_EditVideo,
                LucideIcons.wand_sparkles, ScreenRecordingQuickTaskAction.EditVideo));
            menu.Items.Add(CreateActionItem(Strings.ScreenRecordingQuickTaskMenu_EditWithCapCut,
                LucideIcons.clapperboard, ScreenRecordingQuickTaskAction.EditWithCapCut));
        }

        menu.Items.Add(CreateActionItem(AfterCaptureTasks.CopyFilePathToClipboard.GetLocalizedDescription(),
            LucideIcons.clipboard_list, ScreenRecordingQuickTaskAction.CopyFilePath));
        menu.Items.Add(CreateActionItem(AfterCaptureTasks.CopyFileToClipboard.GetLocalizedDescription(),
            LucideIcons.clipboard, ScreenRecordingQuickTaskAction.CopyFile));

        System.Drawing.Point cursorPosition = CaptureHelpers.GetCursorPosition();
        PixelPoint placementPosition = new(cursorPosition.X, cursorPosition.Y);
        Window placementWindow = new()
        {
            Width = 1,
            Height = 1,
            MinWidth = 1,
            MinHeight = 1,
            CanResize = false,
            ShowInTaskbar = false,
            WindowDecorations = WindowDecorations.None,
            Background = Brushes.Transparent,
            Opacity = 0,
            Topmost = true,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = placementPosition,
            RequestedThemeVariant = ThemeManager.GetCurrentTheme()
        };

        _menu = menu;
        _placementWindow = placementWindow;
        menu.Placement = PlacementMode.AnchorAndGravity;
        menu.PlacementAnchor = PopupAnchor.TopLeft;
        menu.PlacementGravity = PopupGravity.BottomRight;
        menu.PlacementConstraintAdjustment = PopupPositionerConstraintAdjustment.SlideX |
            PopupPositionerConstraintAdjustment.SlideY;
        menu.PlacementTarget = placementWindow;
        // Focus changes while the recording controls close must not dismiss the pending action.
        menu.Closing += (_, e) => e.Cancel = ReferenceEquals(_menu, menu);
        menu.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Select(ScreenRecordingQuickTaskAction.Continue);
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        menu.Loaded += (_, _) =>
        {
            Point? continueCenter = continueItem.TranslatePoint(
                new Point(continueItem.Bounds.Width / 2, continueItem.Bounds.Height / 2), menu);
            if (menu.Parent is Popup popup && continueCenter is Point center)
            {
                popup.HorizontalOffset = -center.X;
                popup.VerticalOffset = -center.Y;
            }
        };
        menu.Opened += (_, _) => DebugHelper.WriteLine("Screen recording quick task menu opened.");
        menu.Closed += (_, _) =>
        {
            DebugHelper.WriteLine("Screen recording quick task menu closed.");
            if (ReferenceEquals(_menu, menu)) _menu = null;
            if (ReferenceEquals(_placementWindow, placementWindow)) _placementWindow = null;
            if (placementWindow.IsVisible) placementWindow.Close();
            Dispatcher.UIThread.Post(() => Select(ScreenRecordingQuickTaskAction.Continue), DispatcherPriority.Background);
        };
        placementWindow.Closed += (_, _) =>
        {
            if (ReferenceEquals(_placementWindow, placementWindow)) _placementWindow = null;
            if (ReferenceEquals(_menu, menu))
            {
                _menu = null;
                menu.Close();
            }
        };
        placementWindow.Show();
        placementWindow.Position = placementPosition;
        placementWindow.Activate();
        menu.Open(placementWindow);
        Dispatcher.UIThread.Post(() => continueItem.Focus(), DispatcherPriority.Input);
    }

    private MenuItem CreateActionItem(string header, string icon, ScreenRecordingQuickTaskAction action)
    {
        TextBlock iconText = new()
        {
            Text = icon,
            FontSize = 16,
            FontWeight = FontWeight.Normal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        iconText.Classes.Add("icon");
        iconText.Classes.Add("accent-menu-icon");

        MenuItem item = new() { Header = header, Icon = iconText };
        item.Classes.Add("compact-menu-item");
        item.Click += (_, _) => Select(action);

        return item;
    }

    private MenuItem CreatePresetItem(QuickTaskInfo preset)
    {
        MenuItem item = new() { Header = preset.ToString() };
        item.Classes.Add("compact-menu-item");
        item.Click += (_, _) => Select(ScreenRecordingQuickTaskAction.Continue, preset);

        return item;
    }

    private static bool IsVideoPreset(QuickTaskInfo preset)
    {
        const AfterCaptureTasks supported = AfterCaptureTasks.SaveImageToFile |
            AfterCaptureTasks.SaveImageToFileWithDialog | AfterCaptureTasks.CopyFileToClipboard |
            AfterCaptureTasks.CopyFilePathToClipboard | AfterCaptureTasks.CopyFolderPathToClipboard |
            AfterCaptureTasks.ShowInExplorer | AfterCaptureTasks.ShowBeforeUploadWindow |
            AfterCaptureTasks.UploadImageToHost | AfterCaptureTasks.DeleteFile;

        return preset.IsValid && (preset.AfterCaptureTasks & ~supported) == AfterCaptureTasks.None;
    }

    private void Select(ScreenRecordingQuickTaskAction action, QuickTaskInfo? preset = null)
    {
        if (_actionSelected) return;
        _actionSelected = true;
        ContextMenu? menu = _menu;
        Window? placementWindow = _placementWindow;
        _menu = null;
        _placementWindow = null;
        menu?.Close();
        placementWindow?.Close();
        Dispatcher.UIThread.Post(() => ActionSelected?.Invoke(action, preset), DispatcherPriority.Background);
    }
}
