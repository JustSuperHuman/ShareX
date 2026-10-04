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

using Microsoft.Win32;
using Newtonsoft.Json;
using ShareX.HelpersLib;
using ShareX.Localization;
using System;
using System.Diagnostics;
using System.IO;
using MessageBox = ShareX.AvaloniaUI.MessageBox;
using MessageBoxButtons = ShareX.AvaloniaUI.MessageBoxButtons;
using MessageBoxIcon = ShareX.AvaloniaUI.MessageBoxIcon;

namespace ShareX
{
    internal static class CapCutIntegration
    {
        public static void OpenVideo(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    throw new FileNotFoundException(null, filePath);
                }

                string executablePath = FindExecutable();
                if (executablePath == null)
                {
                    MessageBox.Show(Strings.CapCutIntegration_NotInstalled, "ShareX",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                ProcessStartInfo startInfo = new ProcessStartInfo(executablePath)
                {
                    UseShellExecute = false
                };
                startInfo.ArgumentList.Add(CreateImportUri(filePath));
                using Process process = Process.Start(startInfo);
            }
            catch (Exception e)
            {
                e.ShowError();
            }
        }

        internal static string CreateImportUri(string filePath)
        {
            // Match CapCut Desktop's Explorer "Edit with CapCut" import link.
            // "sence" is CapCut's spelling. A file URI is required for local media;
            // passing a bare video path to CapCut.exe only opens the application.
            string featureEntry = JsonConvert.SerializeObject(new
            {
                sence = "editor",
                sence_context = new { new_draft = true },
                feature = "import_material_for_editing",
                feature_context = new
                {
                    material_import = true,
                    material_infos = new[] { new { material_uri = new Uri(Path.GetFullPath(filePath)).AbsoluteUri } }
                },
                enter_from = "shell_context_menu",
                extension = new { }
            });

            return "capcut://com.ies.videocut/uganchor/anchor_point/import_material_for_editing?featureEntry=" +
                Uri.EscapeDataString(featureEntry);
        }

        internal static string FindExecutable()
        {
            // The protocol registration also covers custom installation locations.
            string command = RegistryHelpers.GetValueString(@"capcut\shell\open\command", root: RegistryHive.ClassesRoot);
            if (!string.IsNullOrWhiteSpace(command))
            {
                string executablePath = command.ParseQuoteString();
                if (File.Exists(executablePath)) return executablePath;
            }

            const string appPath = @"Software\Microsoft\Windows\CurrentVersion\App Paths\CapCut.exe";
            foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            {
                string executablePath = RegistryHelpers.GetValueString(appPath, root: hive)?.Trim('"');
                if (File.Exists(executablePath)) return executablePath;
            }

            foreach (Environment.SpecialFolder folder in new[]
            {
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolder.ProgramFilesX86
            })
            {
                string root = Environment.GetFolderPath(folder);
                if (string.IsNullOrEmpty(root)) continue;

                foreach (string relativePath in new[] { @"CapCut\Apps\CapCut.exe", @"CapCut\CapCut.exe" })
                {
                    string executablePath = Path.Combine(root, relativePath);
                    if (File.Exists(executablePath)) return executablePath;
                }
            }

            return null;
        }
    }
}
