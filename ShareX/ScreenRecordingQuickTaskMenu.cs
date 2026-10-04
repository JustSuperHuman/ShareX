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

using ShareX.HelpersLib;
using ShareX.Properties;
using System;
using System.Drawing;
using System.Windows.Forms;

namespace ShareX
{
    public enum ScreenRecordingQuickTaskAction
    {
        Continue,
        EditVideo,
        CopyFilePath,
        CopyFile,
        EditWithCapCut
    }

    public sealed class ScreenRecordingQuickTaskMenu
    {
        public event Action<ScreenRecordingQuickTaskAction> ActionSelected;

        public void ShowMenu(string filePath)
        {
            ContextMenuStrip menu = new ContextMenuStrip
            {
                Font = new Font("Arial", 10f),
                AutoClose = false
            };
            bool actionSelected = false;

            void SelectAction(ScreenRecordingQuickTaskAction action)
            {
                if (actionSelected) return;
                actionSelected = true;
                menu.Close();
                ActionSelected?.Invoke(action);
            }

            menu.KeyUp += (sender, e) =>
            {
                if (e.KeyCode == Keys.Escape)
                {
                    menu.Close();
                }
            };
            menu.Closed += (sender, e) =>
            {
                if (!actionSelected)
                {
                    actionSelected = true;
                    ActionSelected?.Invoke(ScreenRecordingQuickTaskAction.Continue);
                }

                // ContextMenuStrip.Close() raises Closed before its internal visibility
                // update has finished. Disposing here makes Close() continue against a
                // disposed control, so defer cleanup until the UI message loop resumes.
                menu.BeginInvoke((Action)menu.Dispose);
            };

            ToolStripMenuItem continueItem = new ToolStripMenuItem(Resources.QuickTaskMenu_ShowMenu_Continue, Resources.control);
            continueItem.Click += (sender, e) => SelectAction(ScreenRecordingQuickTaskAction.Continue);
            menu.Items.Add(continueItem);
            menu.Items.Add(new ToolStripSeparator());

            if (FileHelpers.IsVideoFile(filePath))
            {
                ToolStripMenuItem editItem = new ToolStripMenuItem(Resources.ScreenRecordingQuickTaskMenu_EditVideo, Resources.camcorder_pencil);
                editItem.Click += (sender, e) => SelectAction(ScreenRecordingQuickTaskAction.EditVideo);
                menu.Items.Add(editItem);

                ToolStripMenuItem capCutItem = new ToolStripMenuItem(Resources.ScreenRecordingQuickTaskMenu_EditWithCapCut, Resources.camcorder_pencil);
                capCutItem.Click += (sender, e) => SelectAction(ScreenRecordingQuickTaskAction.EditWithCapCut);
                menu.Items.Add(capCutItem);
            }

            ToolStripMenuItem copyPathItem = new ToolStripMenuItem(
                AfterCaptureTasks.CopyFilePathToClipboard.GetLocalizedDescription(),
                Resources.clipboard_list);
            copyPathItem.Click += (sender, e) => SelectAction(ScreenRecordingQuickTaskAction.CopyFilePath);
            menu.Items.Add(copyPathItem);

            ToolStripMenuItem copyFileItem = new ToolStripMenuItem(
                AfterCaptureTasks.CopyFileToClipboard.GetLocalizedDescription(),
                Resources.clipboard_block);
            copyFileItem.Click += (sender, e) => SelectAction(ScreenRecordingQuickTaskAction.CopyFile);
            menu.Items.Add(copyFileItem);

            ShareXResources.ApplyCustomThemeToContextMenuStrip(menu);
            menu.Items[0].Select();

            Point position = CaptureHelpers.GetCursorPosition();
            position.Offset(-10, -10);
            menu.Show(position);
            menu.Focus();
        }
    }
}
