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
using ShareX.Properties;
using ShareX.ScreenCaptureLib;
using ShareX.Tools;
using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ShareX
{
    public static class ScreenRecordManager
    {
        public static bool IsRecording { get; private set; }

        private static ScreenRecorder screenRecorder;
        private static ScreenRecordForm recordForm;
        private static MouseMotionRecorder mouseMotionRecorder;
        private static ScreenRecordingMotionData pendingMotionData;

        public static void StartStopRecording(ScreenRecordOutput outputType, ScreenRecordStartMethod startMethod, TaskSettings taskSettings)
        {
            if (IsRecording)
            {
                if (recordForm != null && !recordForm.IsDisposed)
                {
                    recordForm.StartStopRecording();
                }
            }
            else
            {
                StartRecording(outputType, taskSettings, startMethod);
            }
        }

        public static void StopRecording()
        {
            if (IsRecording && screenRecorder != null)
            {
                screenRecorder.StopRecording();
            }
        }

        public static void PauseScreenRecording()
        {
            if (IsRecording && recordForm != null && !recordForm.IsDisposed)
            {
                recordForm.PauseResumeRecording();
            }
        }

        public static void AbortRecording()
        {
            if (IsRecording && recordForm != null && !recordForm.IsDisposed)
            {
                recordForm.AbortRecording();
            }
        }

        private static void StartRecording(ScreenRecordOutput outputType, TaskSettings taskSettings, ScreenRecordStartMethod startMethod = ScreenRecordStartMethod.Region)
        {
            if (outputType == ScreenRecordOutput.GIF)
            {
                taskSettings.CaptureSettings.FFmpegOptions.VideoCodec = FFmpegVideoCodec.gif;
            }

            if (taskSettings.CaptureSettings.FFmpegOptions.IsAnimatedImage)
            {
                taskSettings.CaptureSettings.ScreenRecordTwoPassEncoding = true;
            }

            int fps;

            if (taskSettings.CaptureSettings.FFmpegOptions.VideoCodec == FFmpegVideoCodec.gif)
            {
                fps = taskSettings.CaptureSettings.GIFFPS;
            }
            else
            {
                fps = taskSettings.CaptureSettings.ScreenRecordFPS;
            }

            DebugHelper.WriteLine("Starting screen recording. Video encoder: \"{0}\", Audio encoder: \"{1}\", FPS: {2}",
                taskSettings.CaptureSettings.FFmpegOptions.VideoCodec.GetDescription(), taskSettings.CaptureSettings.FFmpegOptions.AudioCodec.GetDescription(), fps);

            if (!TaskHelpers.CheckFFmpeg(taskSettings))
            {
                return;
            }

            if (!taskSettings.CaptureSettings.FFmpegOptions.IsSourceSelected)
            {
                MessageBox.Show(Resources.FFmpeg_FFmpeg_video_and_audio_source_both_can_t_be__None__,
                    "ShareX - " + Resources.FFmpeg_FFmpeg_error, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (taskSettings.GeneralSettings.ToastWindowAutoHide)
            {
                NotificationForm.CloseActiveForm();
            }

            Rectangle captureRectangle = Rectangle.Empty;
            TaskMetadata metadata = new TaskMetadata();

            switch (startMethod)
            {
                case ScreenRecordStartMethod.Region:
                    if (taskSettings.CaptureSettings.ScreenRecordTransparentRegion)
                    {
                        RegionCaptureTasks.GetRectangleRegionTransparent(out captureRectangle);
                    }
                    else
                    {
                        RegionCaptureTasks.GetRectangleRegion(out captureRectangle, out WindowInfo windowInfo, taskSettings.CaptureSettings.SurfaceOptions);

                        metadata.UpdateInfo(windowInfo);
                    }
                    break;
                case ScreenRecordStartMethod.ActiveWindow:
                    if (taskSettings.CaptureSettings.CaptureClientArea)
                    {
                        captureRectangle = CaptureHelpers.GetActiveWindowClientRectangle();
                    }
                    else
                    {
                        captureRectangle = CaptureHelpers.GetActiveWindowRectangle();
                    }

                    IntPtr handle = NativeMethods.GetForegroundWindow();
                    WindowInfo activeWindowInfo = new WindowInfo(handle);
                    metadata.UpdateInfo(activeWindowInfo);
                    break;
                case ScreenRecordStartMethod.CustomRegion:
                    captureRectangle = taskSettings.CaptureSettings.CaptureCustomRegion;
                    break;
                case ScreenRecordStartMethod.LastRegion:
                    captureRectangle = Program.Settings.ScreenRecordRegion;
                    break;
            }

            Rectangle screenRectangle = CaptureHelpers.GetScreenBounds();
            captureRectangle = Rectangle.Intersect(captureRectangle, screenRectangle);

            if (taskSettings.CaptureSettings.FFmpegOptions.IsEvenSizeRequired)
            {
                captureRectangle = CaptureHelpers.EvenRectangleSize(captureRectangle);
            }

            if (IsRecording || !captureRectangle.IsValid() || screenRecorder != null)
            {
                return;
            }

            Program.Settings.ScreenRecordRegion = captureRectangle;

            IsRecording = true;

            string path = "";
            string concatPath = "";
            string tempPath = "";
            bool abortRequested = false;

            float duration = taskSettings.CaptureSettings.ScreenRecordFixedDuration ? taskSettings.CaptureSettings.ScreenRecordDuration : 0;

            recordForm = new ScreenRecordForm(captureRectangle)
            {
                ActivateWindow = startMethod == ScreenRecordStartMethod.Region,
                Duration = duration,
                AskConfirmationOnAbort = taskSettings.CaptureSettings.ScreenRecordAskConfirmationOnAbort
            };

            recordForm.StopRequested += StopRecording;
            recordForm.Show();

            Task.Run(() =>
            {
                try
                {
                    string extension;
                    if (taskSettings.CaptureSettings.ScreenRecordTwoPassEncoding)
                    {
                        extension = "mp4";
                    }
                    else
                    {
                        extension = taskSettings.CaptureSettings.FFmpegOptions.Extension;
                    }
                    string screenshotsFolder = TaskHelpers.GetScreenshotsFolder(taskSettings, metadata);
                    string fileName = TaskHelpers.GetFileName(taskSettings, extension, metadata);
                    path = TaskHelpers.HandleExistsFile(screenshotsFolder, fileName, taskSettings);

                    if (string.IsNullOrEmpty(path))
                    {
                        abortRequested = true;
                    }
                    else
                    {
                        concatPath = FileHelpers.AppendTextToFileName(path, "-concat");
                        FileHelpers.DeleteFile(concatPath);
                        tempPath = FileHelpers.AppendTextToFileName(path, "-temp");
                        FileHelpers.DeleteFile(tempPath);
                    }

                    while (!abortRequested && (recordForm.Status == ScreenRecordingStatus.Waiting || recordForm.Status == ScreenRecordingStatus.Paused))
                    {
                        recordForm.ChangeState(ScreenRecordState.BeforeStart);

                        if (recordForm.Status == ScreenRecordingStatus.Paused || !taskSettings.CaptureSettings.ScreenRecordAutoStart)
                        {
                            recordForm.RecordResetEvent.WaitOne();
                        }
                        else
                        {
                            int delay = (int)(taskSettings.CaptureSettings.ScreenRecordStartDelay * 1000);

                            if (delay > 0)
                            {
                                recordForm.InvokeSafe(() => recordForm.StartCountdown(delay));

                                recordForm.RecordResetEvent.WaitOne(delay);
                            }
                        }

                        if (recordForm.Status == ScreenRecordingStatus.Aborted)
                        {
                            abortRequested = true;
                        }

                        if (recordForm.Status == ScreenRecordingStatus.Waiting || recordForm.Status == ScreenRecordingStatus.Paused)
                        {
                            if (recordForm.Status == ScreenRecordingStatus.Paused && File.Exists(path))
                            {
                                FileHelpers.RenameFile(path, concatPath);
                            }

                            recordForm.ChangeState(ScreenRecordState.AfterStart);

                            captureRectangle = recordForm.RecordingRegion;

                            bool trackMouseMotion = taskSettings.CaptureSettings.ScreenRecordTrackMouseMotion &&
                                outputType != ScreenRecordOutput.GIF && !taskSettings.CaptureSettings.FFmpegOptions.IsAnimatedImage;

                            // The smooth cursor is drawn in afterwards from the motion track, so the real one has to
                            // stay out of the picture. Only for mp4, which is what the cursor pass writes back.
                            bool smoothCursor = trackMouseMotion && taskSettings.CaptureSettings.ScreenRecordShowCursor &&
                                taskSettings.ToolsSettingsReference.VideoEditorOptions.SmoothCursor &&
                                string.Equals(taskSettings.CaptureSettings.FFmpegOptions.Extension, "mp4", StringComparison.OrdinalIgnoreCase);
                            bool drawCursor = taskSettings.CaptureSettings.ScreenRecordShowCursor && !smoothCursor;

                            ScreenRecordingOptions options = new ScreenRecordingOptions()
                            {
                                IsRecording = true,
                                IsLossless = taskSettings.CaptureSettings.ScreenRecordTwoPassEncoding,
                                FFmpeg = taskSettings.CaptureSettings.FFmpegOptions,
                                FPS = fps,
                                Duration = duration,
                                OutputPath = path,
                                CaptureArea = captureRectangle,
                                DrawCursor = drawCursor
                            };

                            Screenshot screenshot = TaskHelpers.GetScreenshot(taskSettings);
                            screenshot.CaptureCursor = drawCursor;

                            screenRecorder?.Dispose();
                            screenRecorder = new ScreenRecorder(ScreenRecordOutput.FFmpeg, options, screenshot, captureRectangle);
                            screenRecorder.RecordingStarted += ScreenRecorder_RecordingStarted;
                            screenRecorder.EncodingProgressChanged += ScreenRecorder_EncodingProgressChanged;

                            // After a pause the new segment is appended to what was recorded so far, so its motion
                            // track has to start where that video ends.
                            bool resumed = File.Exists(concatPath);
                            float motionOffset = 0;

                            if (trackMouseMotion)
                            {
                                if (!resumed)
                                {
                                    pendingMotionData = null;
                                }
                                else if (pendingMotionData != null)
                                {
                                    motionOffset = GetVideoDuration(concatPath, taskSettings);
                                }

                                mouseMotionRecorder?.Dispose();
                                mouseMotionRecorder = new MouseMotionRecorder(captureRectangle, Math.Max(fps, 60));
                            }

                            screenRecorder.StartRecording();

                            if (mouseMotionRecorder != null)
                            {
                                mouseMotionRecorder.Stop();
                                ScreenRecordingMotionData segment = mouseMotionRecorder.GetData();
                                segment.CursorHidden = smoothCursor;

                                if (!resumed)
                                {
                                    pendingMotionData = segment;
                                }
                                else if (pendingMotionData != null && motionOffset > 0)
                                {
                                    pendingMotionData.Append(segment, motionOffset);
                                }
                                else
                                {
                                    pendingMotionData = null;
                                }

                                mouseMotionRecorder.Dispose();
                                mouseMotionRecorder = null;
                            }

                            recordForm.ChangeState(ScreenRecordState.RecordingEnd);

                            if (recordForm.Status == ScreenRecordingStatus.Aborted)
                            {
                                abortRequested = true;
                            }
                        }

                        TaskHelpers.PlayNotificationSoundAsync(NotificationSound.ActionCompleted, taskSettings);

                        if (File.Exists(concatPath))
                        {
                            using (FFmpegCLIManager ffmpeg = new FFmpegCLIManager(taskSettings.CaptureSettings.FFmpegOptions.FFmpegPath))
                            {
                                ffmpeg.ShowError = true;
                                ffmpeg.ConcatenateVideos(new string[] { concatPath, path }, tempPath, true);
                                FileHelpers.RenameFile(tempPath, path);
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    DebugHelper.WriteException(e);
                }

                if (taskSettings.CaptureSettings.ScreenRecordTwoPassEncoding && !abortRequested && screenRecorder != null && File.Exists(path))
                {
                    recordForm.ChangeState(ScreenRecordState.Encoding);

                    path = ProcessTwoPassEncoding(path, metadata, taskSettings);
                }

                if (recordForm != null)
                {
                    recordForm.InvokeSafe(() =>
                    {
                        recordForm.Close();
                        recordForm.Dispose();
                        recordForm = null;
                    });
                }

                if (screenRecorder != null)
                {
                    screenRecorder.Dispose();
                    screenRecorder = null;
                }

                if (abortRequested)
                {
                    FileHelpers.DeleteFile(path);
                }

                FileHelpers.DeleteFile(concatPath);
                FileHelpers.DeleteFile(tempPath);
            }).ContinueInCurrentContext(() => CompleteRecording(path, metadata, taskSettings, abortRequested));
        }

        private static void CompleteRecording(
            string path,
            TaskMetadata metadata,
            TaskSettings taskSettings,
            bool aborted,
            ScreenRecordingQuickTaskAction action = ScreenRecordingQuickTaskAction.Continue,
            bool skipQuickTaskMenu = false,
            bool cursorRendered = false)
        {
            if (!aborted && !skipQuickTaskMenu && !string.IsNullOrEmpty(path) && File.Exists(path) &&
                taskSettings.AfterCaptureJob.HasFlag(AfterCaptureTasks.ShowQuickTaskMenu))
            {
                ScreenRecordingQuickTaskMenu quickTaskMenu = new ScreenRecordingQuickTaskMenu();
                quickTaskMenu.ActionSelected += selectedAction =>
                    CompleteRecording(path, metadata, taskSettings, aborted, selectedAction, true);
                quickTaskMenu.ShowMenu(path);
                return;
            }

            // The editor draws the smooth cursor itself and needs the clean recording; every other route
            // gets the cursor rendered into the file before it is saved, copied or uploaded.
            if (!aborted && !cursorRendered && action != ScreenRecordingQuickTaskAction.EditVideo &&
                pendingMotionData != null && pendingMotionData.CursorHidden && pendingMotionData.HasSamples &&
                !string.IsNullOrEmpty(path) && File.Exists(path))
            {
                ScreenRecordingMotionData motionData = pendingMotionData;
                TaskHelpers.ShowNotificationTip(Resources.ScreenRecordManager_AddingSmoothCursor);

                Task.Run(() => RenderSmoothCursor(path, motionData, taskSettings)).ContinueInCurrentContext(() =>
                    CompleteRecording(path, metadata, taskSettings, aborted, action, true, true));
                return;
            }

            try
            {
                if (!aborted && !string.IsNullOrEmpty(path) && File.Exists(path) &&
                    TaskHelpers.ShowAfterCaptureForm(taskSettings, out string customFileName, null, path))
                {
                    if (!string.IsNullOrEmpty(customFileName))
                    {
                        string currentFileName = Path.GetFileNameWithoutExtension(path);
                        string ext = Path.GetExtension(path);

                        if (!currentFileName.Equals(customFileName, StringComparison.OrdinalIgnoreCase))
                        {
                            path = FileHelpers.RenameFile(path, customFileName + ext);
                        }
                    }

                    SaveMotionData(path);

                    ApplyCompletionActions(taskSettings);
                    ApplyQuickTaskAction(taskSettings, action);

                    WorkerTask task = WorkerTask.CreateFileJobTask(path, metadata, taskSettings, customFileName);
                    TaskManager.Start(task);

                    if (action == ScreenRecordingQuickTaskAction.EditVideo)
                    {
                        TaskHelpers.OpenVideoEditor(path, taskSettings);
                    }
                    else if (action == ScreenRecordingQuickTaskAction.EditWithCapCut)
                    {
                        CapCutIntegration.OpenVideo(path);
                    }
                }
            }
            finally
            {
                IsRecording = false;
                pendingMotionData = null;
            }
        }

        // Re-encodes the recording with the smooth cursor drawn in and swaps it into place. On any failure
        // the clean recording is left untouched, and its motion track still lets the editor add the cursor.
        private static void RenderSmoothCursor(string path, ScreenRecordingMotionData motionData, TaskSettings taskSettings)
        {
            string ffmpegPath = taskSettings.CaptureSettings.FFmpegOptions.FFmpegPath;
            string outputPath = FileHelpers.AppendTextToFileName(path, "-cursor");
            VideoEditorExportRequest request = null;

            try
            {
                VideoInfo info;

                using (FFmpegCLIManager probe = new FFmpegCLIManager(ffmpegPath) { ShowError = false })
                {
                    info = probe.GetVideoInfo(path);
                }

                if (info == null || info.VideoResolution.IsEmpty)
                {
                    return;
                }

                request = VideoCursorBaker.BuildRequest(path, outputPath, motionData, taskSettings.ToolsSettingsReference.VideoEditorOptions,
                    info.VideoResolution.Width, info.VideoResolution.Height, info.Duration.TotalSeconds);

                if (request == null)
                {
                    return;
                }

                using (FFmpegCLIManager ffmpeg = new FFmpegCLIManager(ffmpegPath) { ShowError = false })
                {
                    if (ffmpeg.Run(request.Arguments) && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
                    {
                        File.Move(outputPath, path, true);
                        motionData.CursorHidden = false;
                    }
                    else
                    {
                        DebugHelper.WriteLine("Smooth cursor render failed: " + ffmpeg.Output);
                    }
                }
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
            }
            finally
            {
                if (request?.TempFiles != null)
                {
                    foreach (string tempFile in request.TempFiles)
                    {
                        FileHelpers.DeleteFile(tempFile);
                    }
                }

                FileHelpers.DeleteFile(outputPath);
            }
        }

        private static float GetVideoDuration(string path, TaskSettings taskSettings)
        {
            try
            {
                using (FFmpegCLIManager ffmpeg = new FFmpegCLIManager(taskSettings.CaptureSettings.FFmpegOptions.FFmpegPath) { ShowError = false })
                {
                    VideoInfo info = ffmpeg.GetVideoInfo(path);
                    return info != null ? (float)info.Duration.TotalSeconds : 0;
                }
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
                return 0;
            }
        }

        private static void SaveMotionData(string path)
        {
            if (pendingMotionData != null && pendingMotionData.HasSamples && !string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try
                {
                    pendingMotionData.Save(path);
                }
                catch (Exception e)
                {
                    DebugHelper.WriteException(e);
                }
            }
        }

        private static void ApplyQuickTaskAction(TaskSettings taskSettings, ScreenRecordingQuickTaskAction action)
        {
            if (action is ScreenRecordingQuickTaskAction.EditVideo or ScreenRecordingQuickTaskAction.EditWithCapCut)
            {
                taskSettings.AfterCaptureJob = taskSettings.AfterCaptureJob.Remove(AfterCaptureTasks.DeleteFile);
            }
            else if (action is ScreenRecordingQuickTaskAction.CopyFilePath or ScreenRecordingQuickTaskAction.CopyFile)
            {
                taskSettings.AfterCaptureJob = taskSettings.AfterCaptureJob
                    .Remove(AfterCaptureTasks.CopyFileToClipboard)
                    .Remove(AfterCaptureTasks.CopyFilePathToClipboard)
                    .Remove(AfterCaptureTasks.CopyFolderPathToClipboard)
                    .Add(action == ScreenRecordingQuickTaskAction.CopyFilePath
                        ? AfterCaptureTasks.CopyFilePathToClipboard
                        : AfterCaptureTasks.CopyFileToClipboard);
            }
        }

        private static void ApplyCompletionActions(TaskSettings taskSettings)
        {
            if (taskSettings.CaptureSettings.ScreenRecordCopyFilePathToClipboard)
            {
                taskSettings.AfterCaptureJob = taskSettings.AfterCaptureJob
                    .Remove(AfterCaptureTasks.CopyFileToClipboard)
                    .Remove(AfterCaptureTasks.CopyFolderPathToClipboard)
                    .Add(AfterCaptureTasks.CopyFilePathToClipboard);
            }

            if (taskSettings.CaptureSettings.ScreenRecordOpenFolderOnNotificationClick)
            {
                taskSettings.GeneralSettings.ToastWindowLeftClickAction = ToastClickAction.OpenFolder;
            }
        }

        private static void ScreenRecorder_RecordingStarted()
        {
            mouseMotionRecorder?.Start();
            recordForm.ChangeState(ScreenRecordState.AfterRecordingStart);
        }

        private static void ScreenRecorder_EncodingProgressChanged(int progress)
        {
            recordForm.ChangeStateProgress(progress);
        }

        private static string ProcessTwoPassEncoding(string input, TaskMetadata metadata, TaskSettings taskSettings, bool deleteInputFile = true)
        {
            string screenshotsFolder = TaskHelpers.GetScreenshotsFolder(taskSettings, metadata);
            string fileName = TaskHelpers.GetFileName(taskSettings, taskSettings.CaptureSettings.FFmpegOptions.Extension, metadata);
            string output = Path.Combine(screenshotsFolder, fileName);

            try
            {
                if (taskSettings.CaptureSettings.FFmpegOptions.VideoCodec == FFmpegVideoCodec.gif)
                {
                    screenRecorder.FFmpegEncodeAsGIF(input, output);
                }
                else
                {
                    screenRecorder.FFmpegEncodeVideo(input, output);
                }
            }
            finally
            {
                if (deleteInputFile && !input.Equals(output, StringComparison.OrdinalIgnoreCase) && File.Exists(input))
                {
                    File.Delete(input);
                }
            }

            return output;
        }
    }
}
