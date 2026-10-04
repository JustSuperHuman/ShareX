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

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ShareX.ShellExtension;

// The "Edit with ShareX" entry in the Windows 11 File Explorer context menu for video files.
// Explorer activates this class out-of-process through the COM surrogate declared in
// AppxManifest.xml and calls IExplorerCommand on it; Invoke hands the file to the ShareX video
// editor. The DLL lives next to ShareX.exe, which is how it finds the executable to launch.
[ComVisible(true)]
[Guid(Clsid)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class VideoEditorCommand : IExplorerCommand
{
    public const string Clsid = "71102C74-9458-49E2-B70B-2A0E790ED36B";

    private const int S_OK = 0;
    private const int E_NOTIMPL = unchecked((int)0x80004001);
    private const uint SIGDN_FILESYSPATH = 0x80058000;

    // AppContext.BaseDirectory is unreliable inside the COM surrogate, so anchor on this DLL's own
    // location — it is deployed next to ShareX.exe.
    private static readonly string BaseDirectory =
        Path.GetDirectoryName(typeof(VideoEditorCommand).Assembly.Location) ?? AppContext.BaseDirectory;

    private static string ShareXExecutablePath => Path.Combine(BaseDirectory, "ShareX.exe");

    public int GetTitle(IShellItemArray? psiItemArray, out IntPtr ppszName)
    {
        ppszName = Marshal.StringToCoTaskMemUni("Edit with ShareX");
        return S_OK;
    }

    public int GetIcon(IShellItemArray? psiItemArray, out IntPtr ppszIcon)
    {
        ppszIcon = Marshal.StringToCoTaskMemUni($"{ShareXExecutablePath},0");
        return S_OK;
    }

    public int GetToolTip(IShellItemArray? psiItemArray, out IntPtr ppszInfotip)
    {
        ppszInfotip = IntPtr.Zero;
        return E_NOTIMPL;
    }

    public int GetCanonicalName(out Guid pguidCommandName)
    {
        pguidCommandName = new Guid(Clsid);
        return S_OK;
    }

    public int GetState(IShellItemArray? psiItemArray, bool fOkToBeSlow, out uint pCmdState)
    {
        pCmdState = 0; // ECS_ENABLED
        return S_OK;
    }

    public int Invoke(IShellItemArray? psiItemArray, IntPtr pbc)
    {
        try
        {
            if (psiItemArray == null) return S_OK;

            psiItemArray.GetCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                if (psiItemArray.GetItemAt(i, out IShellItem item) != S_OK) continue;
                if (item.GetDisplayName(SIGDN_FILESYSPATH, out IntPtr pathPtr) != S_OK) continue;

                string? filePath = Marshal.PtrToStringUni(pathPtr);
                Marshal.FreeCoTaskMem(pathPtr);
                if (string.IsNullOrEmpty(filePath)) continue;

                ProcessStartInfo psi = new(ShareXExecutablePath, $"-VideoEditor \"{filePath}\"")
                {
                    UseShellExecute = false,
                    WorkingDirectory = BaseDirectory
                };
                Process.Start(psi);
            }
        }
        catch
        {
            // Never let an exception escape into Explorer's surrogate.
        }

        return S_OK;
    }

    public int GetFlags(out uint pFlags)
    {
        pFlags = 0; // ECF_DEFAULT
        return S_OK;
    }

    public int EnumSubCommands(out IntPtr ppEnum)
    {
        ppEnum = IntPtr.Zero;
        return E_NOTIMPL;
    }
}

#region Shell COM interop

[ComImport]
[Guid("a08ce4d0-fa25-44ab-b57c-c7b1c323e0b9")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IExplorerCommand
{
    [PreserveSig] int GetTitle(IShellItemArray? psiItemArray, out IntPtr ppszName);
    [PreserveSig] int GetIcon(IShellItemArray? psiItemArray, out IntPtr ppszIcon);
    [PreserveSig] int GetToolTip(IShellItemArray? psiItemArray, out IntPtr ppszInfotip);
    [PreserveSig] int GetCanonicalName(out Guid pguidCommandName);
    [PreserveSig] int GetState(IShellItemArray? psiItemArray, [MarshalAs(UnmanagedType.Bool)] bool fOkToBeSlow, out uint pCmdState);
    [PreserveSig] int Invoke(IShellItemArray? psiItemArray, IntPtr pbc);
    [PreserveSig] int GetFlags(out uint pFlags);
    [PreserveSig] int EnumSubCommands(out IntPtr ppEnum);
}

[ComImport]
[Guid("b63ea76d-1f85-456f-a19c-48159efa858b")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItemArray
{
    [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppvOut);
    [PreserveSig] int GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetAttributes(int attribFlags, uint sfgaoMask, out uint psfgaoAttribs);
    [PreserveSig] int GetCount(out uint pdwNumItems);
    [PreserveSig] int GetItemAt(uint dwIndex, out IShellItem ppsi);
    [PreserveSig] int EnumItems(out IntPtr ppenumShellItems);
}

[ComImport]
[Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItem
{
    [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetParent(out IShellItem ppsi);
    [PreserveSig] int GetDisplayName(uint sigdnName, out IntPtr ppszName);
    [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
    [PreserveSig] int Compare(IShellItem psi, uint hint, out int piOrder);
}

#endregion
