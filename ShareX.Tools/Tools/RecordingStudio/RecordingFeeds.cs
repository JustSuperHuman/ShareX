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

namespace ShareX.Tools;

// Runs ffmpeg against a capture device and hands its raw output to a reader on a background thread.
// ffmpeg is already how ShareX talks to DirectShow, so live previews need no extra dependency.
public abstract class FFmpegDeviceFeed : IDisposable
{
    private Process? _process;
    private Thread? _thread;
    private volatile bool _stopped;

    public event Action? Failed;

    protected void Start(string ffmpegPath, string arguments, string threadName)
    {
        try
        {
            _process = new Process
            {
                StartInfo = new ProcessStartInfo(ffmpegPath, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true
                }
            };
            _process.ErrorDataReceived += (_, _) => { };
            _process.Start();
            _process.BeginErrorReadLine();
        }
        catch (Exception e)
        {
            DebugHelper.WriteException(e);
            Failed?.Invoke();
            return;
        }

        _thread = new Thread(ReadLoop) { IsBackground = true, Name = threadName };
        _thread.Start();
    }

    private void ReadLoop()
    {
        try
        {
            Read(_process!.StandardOutput.BaseStream);
        }
        catch (Exception e)
        {
            if (!_stopped) DebugHelper.WriteException(e);
        }

        if (!_stopped) Failed?.Invoke();
    }

    protected abstract void Read(Stream stream);

    protected bool Stopped => _stopped;

    protected static bool ReadExactly(Stream stream, byte[] buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read <= 0) return false;
            offset += read;
        }
        return true;
    }

    public void Dispose()
    {
        if (_stopped) return;
        _stopped = true;

        try
        {
            if (_process is { HasExited: false })
            {
                // "q" lets ffmpeg release the device cleanly; kill it if it doesn't take the hint.
                _process.StandardInput.Write('q');
                _process.StandardInput.Flush();
                if (!_process.WaitForExit(700)) _process.Kill();
            }
        }
        catch
        {
            try { _process?.Kill(); } catch { }
        }

        _process?.Dispose();
        _process = null;
    }
}

// Live BGRA frames from a camera, scaled down to preview size.
public sealed class CameraFeed : FFmpegDeviceFeed
{
    public const int FrameHeight = 480;

    public int FrameWidth { get; }

    // Raised on a background thread with a buffer that is reused for the next frame.
    public event Action<byte[]>? FrameReady;

    public CameraFeed(string ffmpegPath, string device, CameraFormat? format)
    {
        double aspect = format != null && format.Height > 0 ? format.Width / (double)format.Height : 16d / 9d;
        FrameWidth = (int)Math.Round(FrameHeight * aspect / 2) * 2;

        string input = format != null
            ? $"-video_size {format.Width}x{format.Height} -framerate {format.Fps.ToString("0.###", CultureInfo.InvariantCulture)} "
            : string.Empty;

        Start(ffmpegPath,
            $"-hide_banner -loglevel error -f dshow -rtbufsize 64M -fflags nobuffer {input}-i video={RecordingDevices.EscapeDevice(device)} " +
            $"-vf \"fps=30,scale={FrameWidth}:{FrameHeight}\" -pix_fmt bgra -f rawvideo -",
            "CameraFeed");
    }

    protected override void Read(Stream stream)
    {
        byte[] frame = new byte[FrameWidth * FrameHeight * 4];
        while (!Stopped && ReadExactly(stream, frame))
        {
            FrameReady?.Invoke(frame);
        }
    }
}

// Reports how loud a microphone is, as a 0..1 level for a meter.
public sealed class MicrophoneLevelFeed : FFmpegDeviceFeed
{
    private const int SampleRate = 16000;
    private const int SamplesPerReading = SampleRate / 20;

    // Raised on a background thread about 20 times a second.
    public event Action<double>? LevelChanged;

    public MicrophoneLevelFeed(string ffmpegPath, string device)
    {
        Start(ffmpegPath,
            $"-hide_banner -loglevel error -f dshow -audio_buffer_size 50 -i audio={RecordingDevices.EscapeDevice(device)} " +
            $"-ac 1 -ar {SampleRate} -f s16le -",
            "MicrophoneLevelFeed");
    }

    protected override void Read(Stream stream)
    {
        byte[] buffer = new byte[SamplesPerReading * 2];
        while (!Stopped && ReadExactly(stream, buffer))
        {
            int peak = 0;
            for (int i = 0; i < buffer.Length; i += 2)
            {
                int sample = Math.Abs((int)BitConverter.ToInt16(buffer, i));
                if (sample > peak) peak = sample;
            }

            // -40 dBFS and below reads as silence, 0 dBFS as full; the square root keeps speech visible.
            double db = peak > 0 ? 20 * Math.Log10(peak / 32768.0) : -96;
            double level = Math.Sqrt(Math.Clamp((db + 40) / 40, 0, 1));
            LevelChanged?.Invoke(level);
        }
    }
}
