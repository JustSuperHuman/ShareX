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
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace ShareX.Tools;

public sealed record CameraFormat(int Width, int Height, double Fps)
{
    public override string ToString() => $"{Width} × {Height} @ {Fps:0.##} fps";
}

public sealed record MicrophoneFormat(int SampleRate, int Channels)
{
    public override string ToString()
    {
        string channels = Channels switch { 1 => "Mono", 2 => "Stereo", _ => $"{Channels} ch" };
        return $"{SampleRate / 1000.0:0.#} kHz {channels}";
    }
}

// Finds cameras, microphones and displays, and asks ffmpeg what each device can do.
public static partial class RecordingDevices
{
    // DirectShow devices that capture the screen or the system's own sound rather than a real
    // camera or microphone.
    public const string SystemAudioDevice = "virtual-audio-capturer";
    private const string ScreenCaptureDevice = "screen-capture-recorder";

    // Id is what ffmpeg is given; Name is what the user sees. They differ for devices with a colon in
    // their name, which ffmpeg's DirectShow input can only open through the alternative name.
    public sealed record Device(string Id, string Name);

    public static (List<Device> Cameras, List<Device> Microphones, bool SystemAudioAvailable) ListDevices(string ffmpegPath)
    {
        List<Device> cameras = [];
        List<Device> microphones = [];
        bool systemAudio = false;

        string[] lines = RunFFmpeg(ffmpegPath, "-hide_banner -f dshow -list_devices true -i dummy").Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            Match match = DeviceRegex().Match(lines[i]);
            if (!match.Success) continue;

            string name = match.Groups["name"].Value;
            string id = name;

            if (name.Contains(':') && i + 1 < lines.Length)
            {
                Match alternative = AlternativeNameRegex().Match(lines[i + 1]);
                if (alternative.Success) id = alternative.Groups["name"].Value;
            }

            if (match.Groups["kind"].Value == "video")
            {
                if (!name.Equals(ScreenCaptureDevice, StringComparison.OrdinalIgnoreCase)) cameras.Add(new Device(id, name));
            }
            else if (name.Equals(SystemAudioDevice, StringComparison.OrdinalIgnoreCase))
            {
                systemAudio = true;
            }
            else
            {
                microphones.Add(new Device(id, name));
            }
        }

        return (cameras, microphones, systemAudio);
    }

    // The format a camera bubble should ask for: smooth and no larger than 720p, since it is shown small.
    public static CameraFormat? GetCameraFormat(string ffmpegPath, string device)
    {
        List<CameraFormat> formats = [];

        foreach (Match match in CameraFormatRegex().Matches(RunListOptions(ffmpegPath, "video", device)))
        {
            formats.Add(new CameraFormat(
                int.Parse(match.Groups["w"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture),
                double.Parse(match.Groups["fps"].Value, CultureInfo.InvariantCulture)));
        }

        if (formats.Count == 0) return null;

        return formats
            .OrderByDescending(f => f.Fps >= 24 && f.Height <= 720)
            .ThenByDescending(f => Math.Min(f.Fps, 30))
            .ThenByDescending(f => f.Height <= 720 ? f.Height : -f.Height)
            .First();
    }

    public static MicrophoneFormat? GetMicrophoneFormat(string ffmpegPath, string device)
    {
        MicrophoneFormat? best = null;

        foreach (Match match in MicrophoneFormatRegex().Matches(RunListOptions(ffmpegPath, "audio", device)))
        {
            MicrophoneFormat format = new(
                int.Parse(match.Groups["rate"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["ch"].Value, CultureInfo.InvariantCulture));

            if (best == null || format.SampleRate > best.SampleRate || (format.SampleRate == best.SampleRate && format.Channels > best.Channels))
            {
                best = format;
            }
        }

        return best;
    }

    public static string EscapeDevice(string device) => "\"" + device.Replace("\"", "\\\"") + "\"";

    private static string RunListOptions(string ffmpegPath, string kind, string device) =>
        RunFFmpeg(ffmpegPath, $"-hide_banner -f dshow -list_options true -i {kind}={EscapeDevice(device)}");

    private static string RunFFmpeg(string ffmpegPath, string arguments)
    {
        try
        {
            using Process process = new();
            process.StartInfo = new ProcessStartInfo(ffmpegPath, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                StandardErrorEncoding = Encoding.UTF8
            };
            process.Start();
            string output = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(5000)) process.Kill();
            return output;
        }
        catch (Exception e)
        {
            DebugHelper.WriteException(e);
            return string.Empty;
        }
    }

    [GeneratedRegex("\"(?<name>.+)\" \\((?<kind>video|audio)\\)")]
    private static partial Regex DeviceRegex();

    [GeneratedRegex("Alternative name \"(?<name>.+)\"")]
    private static partial Regex AlternativeNameRegex();

    [GeneratedRegex(@"max s=(?<w>\d+)x(?<h>\d+) fps=(?<fps>[\d.]+)")]
    private static partial Regex CameraFormatRegex();

    [GeneratedRegex(@"ch=\s*(?<ch>\d+), bits=\s*\d+, rate=\s*(?<rate>\d+)")]
    private static partial Regex MicrophoneFormatRegex();

    public static List<RecordingDisplayItem> ListDisplays()
    {
        DesktopScreen[] screens = DesktopScreen.AllScreens;
        List<RecordingDisplayItem> displays = [];

        for (int i = 0; i < screens.Length; i++)
        {
            DesktopScreen screen = screens[i];
            displays.Add(new RecordingDisplayItem($"Display {i + 1}", screen.Bounds, GetRefreshRate(screen.Bounds), screen.Primary));
        }

        return displays;
    }

    private static int GetRefreshRate(System.Drawing.Rectangle bounds)
    {
        try
        {
            POINT center = new() { X = bounds.X + bounds.Width / 2, Y = bounds.Y + bounds.Height / 2 };
            IntPtr monitor = MonitorFromPoint(center, 2);
            MONITORINFOEX info = new() { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return 0;

            DEVMODE mode = new() { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            return EnumDisplaySettings(info.szDevice, -1, ref mode) ? mode.dmDisplayFrequency : 0;
        }
        catch
        {
            return 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);
}
