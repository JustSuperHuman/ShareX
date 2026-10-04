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

// The menu shown at the cursor when a screen recording finishes. Closing it without picking anything
// continues with the normal after-capture tasks, so a recording is never left hanging.
public sealed class ScreenRecordingQuickTaskMenu
{
    public event Action<ScreenRecordingQuickTaskAction>? ActionSelected;

    private bool _actionSelected;

    public void ShowMenu(string filePath)
    {
        Dispatcher.UIThread.Post(() => ShowMenuCore(filePath));
    }

    private void ShowMenuCore(string filePath)
    {
        ContextMenu menu = new();
        MenuItem continueItem = CreateActionItem(menu, Strings.QuickTaskMenu_ShowMenu_Continue,
            LucideIcons.circle_play, ScreenRecordingQuickTaskAction.Continue);
        menu.Items.Add(continueItem);
        menu.Items.Add(new Separator());

        if (FileHelpers.IsVideoFile(filePath))
        {
            menu.Items.Add(CreateActionItem(menu, Strings.ScreenRecordingQuickTaskMenu_EditVideo,
                LucideIcons.wand_sparkles, ScreenRecordingQuickTaskAction.EditVideo));
            menu.Items.Add(CreateActionItem(menu, Strings.ScreenRecordingQuickTaskMenu_EditWithCapCut,
                LucideIcons.clapperboard, ScreenRecordingQuickTaskAction.EditWithCapCut));
        }

        menu.Items.Add(CreateActionItem(menu, AfterCaptureTasks.CopyFilePathToClipboard.GetLocalizedDescription(),
            LucideIcons.clipboard_list, ScreenRecordingQuickTaskAction.CopyFilePath));
        menu.Items.Add(CreateActionItem(menu, AfterCaptureTasks.CopyFileToClipboard.GetLocalizedDescription(),
            LucideIcons.clipboard, ScreenRecordingQuickTaskAction.CopyFile));

        System.Drawing.Point cursorPosition = CaptureHelpers.GetCursorPosition();
        PixelPoint placementPosition = new(cursorPosition.X - 10, cursorPosition.Y - 10);
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

        menu.Placement = PlacementMode.BottomEdgeAlignedLeft;
        menu.PlacementTarget = placementWindow;
        menu.Closed += (_, _) =>
        {
            if (placementWindow.IsVisible) placementWindow.Close();
            Dispatcher.UIThread.Post(() => Select(ScreenRecordingQuickTaskAction.Continue), DispatcherPriority.Background);
        };
        placementWindow.Closed += (_, _) => menu.Close();
        placementWindow.Show();
        placementWindow.Position = placementPosition;
        placementWindow.Activate();
        menu.Open(placementWindow);
        Dispatcher.UIThread.Post(() => continueItem.Focus(), DispatcherPriority.Input);
    }

    private MenuItem CreateActionItem(ContextMenu menu, string header, string icon, ScreenRecordingQuickTaskAction action)
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
        item.Click += (_, _) =>
        {
            // Claim the action before the menu's Closed handler falls back to Continue.
            bool first = !_actionSelected;
            _actionSelected = true;
            menu.Close();
            if (first) Dispatcher.UIThread.Post(() => ActionSelected?.Invoke(action), DispatcherPriority.Background);
        };

        return item;
    }

    private void Select(ScreenRecordingQuickTaskAction action)
    {
        if (_actionSelected) return;
        _actionSelected = true;
        ActionSelected?.Invoke(action);
    }
}
