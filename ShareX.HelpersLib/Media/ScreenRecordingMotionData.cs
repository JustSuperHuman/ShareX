#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using System;
using System.Collections.Generic;
using System.IO;

namespace ShareX.HelpersLib
{
    // A single sampled cursor position, timestamped from the start of the recording.
    // Coordinates are normalized (0..1) inside the captured region so they survive scaling.
    public class ScreenRecordingMotionSample
    {
        public float T { get; set; }
        public float X { get; set; }
        public float Y { get; set; }

        public ScreenRecordingMotionSample()
        {
        }

        public ScreenRecordingMotionSample(float t, float x, float y)
        {
            T = t;
            X = x;
            Y = y;
        }
    }

    // A mouse-down event, used to emphasize points where the user was acting.
    public class ScreenRecordingMotionClick
    {
        public float T { get; set; }
        public float X { get; set; }
        public float Y { get; set; }

        public ScreenRecordingMotionClick()
        {
        }

        public ScreenRecordingMotionClick(float t, float x, float y)
        {
            T = t;
            X = x;
            Y = y;
        }
    }

    // Cursor motion recorded alongside a screen recording. Persisted next to the video file as a
    // sidecar so the video editor can drive elegant auto-zoom that follows what the user was doing.
    public class ScreenRecordingMotionData
    {
        public const string SidecarSuffix = ".sharex-motion.json";

        public int Version { get; set; } = 1;
        public int CaptureWidth { get; set; }
        public int CaptureHeight { get; set; }
        public float Duration { get; set; }
        public List<ScreenRecordingMotionSample> Samples { get; set; } = new List<ScreenRecordingMotionSample>();
        public List<ScreenRecordingMotionClick> Clicks { get; set; } = new List<ScreenRecordingMotionClick>();

        public bool HasSamples => Samples != null && Samples.Count > 1;

        public static string GetSidecarPath(string videoFilePath)
        {
            return string.IsNullOrEmpty(videoFilePath) ? null : videoFilePath + SidecarSuffix;
        }

        public void Save(string videoFilePath)
        {
            string path = GetSidecarPath(videoFilePath);

            if (!string.IsNullOrEmpty(path))
            {
                JsonHelpers.SerializeToFile(this, path);
            }
        }

        public static ScreenRecordingMotionData Load(string videoFilePath)
        {
            string path = GetSidecarPath(videoFilePath);

            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try
                {
                    return JsonHelpers.DeserializeFromFile<ScreenRecordingMotionData>(path);
                }
                catch
                {
                }
            }

            return null;
        }

        public static void Delete(string videoFilePath)
        {
            string path = GetSidecarPath(videoFilePath);

            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                FileHelpers.DeleteFile(path);
            }
        }
    }
}
