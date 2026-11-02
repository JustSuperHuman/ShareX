#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License as published by
    the Free Software Foundation; either version 2 of the License, or
    (at your option) any later version.
*/

#endregion License Information (GPL v3)

using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX.HelpersLib;

/// <summary>Keeps Windows screen snipping from intercepting ShareX's Print Screen hotkeys.</summary>
public sealed class PrintScreenHotkeyGuard : IDisposable
{
    private const string KeyboardKey = @"Control Panel\Keyboard";
    private const string SnippingValue = "PrintScreenKeyForSnippingEnabled";
    private const uint NotifyLastSet = 0x00000004;
    private const uint NotifyThreadAgnostic = 0x10000000;

    private readonly object sync = new();
    private readonly Action settingRestored;
    private readonly RegistryKey keyboard;
    private readonly AutoResetEvent changed;
    private RegisteredWaitHandle registration;
    private bool disposed;

    public PrintScreenHotkeyGuard(Action settingRestored)
    {
        this.settingRestored = settingRestored;
        keyboard = Registry.CurrentUser.CreateSubKey(KeyboardKey, writable: true);
        changed = new AutoResetEvent(false);
        try
        {
            lock (sync)
            {
                ArmWatch();
                registration = ThreadPool.RegisterWaitForSingleObject(changed, OnRegistryChanged, null,
                    Timeout.Infinite, executeOnlyOnce: false);
                RestoreSetting();
            }
            // Refresh the shell even when the value was already disabled before ShareX started.
            NotifyShell();
            DebugHelper.WriteLine("Print Screen hotkey protection enabled.");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void ArmWatch()
    {
        int error = RegNotifyChangeKeyValue(keyboard.Handle, false, NotifyLastSet | NotifyThreadAgnostic,
            changed.SafeWaitHandle, true);
        if (error != 0) throw new Win32Exception(error);
    }

    private bool RestoreSetting()
    {
        if (keyboard.GetValue(SnippingValue) is int value && value == 0) return false;
        keyboard.SetValue(SnippingValue, 0, RegistryValueKind.DWord);
        DebugHelper.WriteLine("Disabled Windows Print Screen interception for ShareX.");
        return true;
    }

    private void OnRegistryChanged(object state, bool timedOut)
    {
        try
        {
            bool restored;
            lock (sync)
            {
                if (disposed) return;
                // Rearm before reading so a change during the repair is also observed.
                ArmWatch();
                restored = RestoreSetting();
            }
            if (restored) NotifyShell();
        }
        catch (Exception ex)
        {
            DebugHelper.WriteException(ex, "Unable to protect the Print Screen hotkey.");
        }
    }

    private void NotifyShell()
    {
        _ = Task.Run(() =>
        {
            IntPtr section = Marshal.StringToHGlobalUni("Keyboard");
            try
            {
                NativeMethods.SendMessageTimeout(new IntPtr(0xffff), (uint)WindowsMessages.SETTINGCHANGE,
                    IntPtr.Zero, section, SendMessageTimeoutFlags.SMTO_ABORTIFHUNG, 200, out _);
                lock (sync)
                {
                    if (!disposed) settingRestored?.Invoke();
                }
            }
            catch (Exception ex)
            {
                DebugHelper.WriteException(ex, "Unable to refresh the Print Screen hotkey.");
            }
            finally
            {
                Marshal.FreeHGlobal(section);
            }
        });
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            registration?.Unregister(null);
            keyboard.Dispose();
            changed.Dispose();
        }
    }

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern int RegNotifyChangeKeyValue(SafeRegistryHandle key,
        [MarshalAs(UnmanagedType.Bool)] bool watchSubtree, uint notifyFilter, SafeWaitHandle changeEvent,
        [MarshalAs(UnmanagedType.Bool)] bool asynchronous);
}
