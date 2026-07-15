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

using ShareX.HelpersLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace ShareX.ScreenCaptureLib
{
    // Samples the cursor position (and mouse clicks) on a background thread while a screen recording is
    // in progress. The resulting motion track lets the video editor drive elegant auto-zoom later on.
    public class MouseMotionRecorder : IDisposable
    {
        private readonly Rectangle captureArea;
        private readonly int intervalMilliseconds;
        private readonly List<ScreenRecordingMotionSample> samples = new List<ScreenRecordingMotionSample>();
        private readonly List<ScreenRecordingMotionClick> clicks = new List<ScreenRecordingMotionClick>();

        private Thread thread;
        private Stopwatch stopwatch;
        private volatile bool running;
        private float lastElapsedSeconds;

        public MouseMotionRecorder(Rectangle captureArea, int sampleRateHz = 60)
        {
            this.captureArea = captureArea;
            intervalMilliseconds = Math.Max(4, 1000 / Math.Max(1, sampleRateHz));
        }

        public void Start()
        {
            if (running || captureArea.Width <= 0 || captureArea.Height <= 0)
            {
                return;
            }

            running = true;
            stopwatch = Stopwatch.StartNew();
            thread = new Thread(SampleLoop)
            {
                IsBackground = true,
                Name = "MouseMotionRecorder",
                Priority = ThreadPriority.BelowNormal
            };
            thread.Start();
        }

        public void Stop()
        {
            if (!running)
            {
                return;
            }

            running = false;

            try
            {
                thread?.Join(1000);
            }
            catch
            {
            }
        }

        private void SampleLoop()
        {
            bool previousLeftDown = false;

            while (running)
            {
                float t = (float)stopwatch.Elapsed.TotalSeconds;
                lastElapsedSeconds = t;

                Point position = CaptureHelpers.GetCursorPosition();
                float x = Clamp01((position.X - captureArea.X) / (float)captureArea.Width);
                float y = Clamp01((position.Y - captureArea.Y) / (float)captureArea.Height);

                samples.Add(new ScreenRecordingMotionSample(t, x, y));

                bool leftDown = (Control.MouseButtons & MouseButtons.Left) == MouseButtons.Left;

                if (leftDown && !previousLeftDown)
                {
                    clicks.Add(new ScreenRecordingMotionClick(t, x, y));
                }

                previousLeftDown = leftDown;

                Thread.Sleep(intervalMilliseconds);
            }
        }

        // Returns the recorded track. Call after Stop().
        public ScreenRecordingMotionData GetData()
        {
            return new ScreenRecordingMotionData
            {
                CaptureWidth = captureArea.Width,
                CaptureHeight = captureArea.Height,
                Duration = lastElapsedSeconds,
                Samples = samples,
                Clicks = clicks
            };
        }

        private static float Clamp01(float value)
        {
            if (value < 0f) return 0f;
            if (value > 1f) return 1f;
            return value;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
