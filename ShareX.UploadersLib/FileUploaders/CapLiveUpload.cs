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

using Newtonsoft.Json;
using ShareX.HelpersLib;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace ShareX.UploadersLib.FileUploaders
{
    // Sends a recording to Cap while it is still being recorded. The video is created on the server
    // when recording starts and every few megabytes ffmpeg writes go up as another part, so when the
    // recording stops only the tail is left and the share link is ready almost at once.
    //
    // This only works for files that are written strictly front to back (fragmented MP4): bytes that
    // have been uploaded must never change afterwards.
    public sealed class CapLiveUpload
    {
        // S3 needs every part except the last to be at least 5 MiB.
        private const int PartSize = 6 * 1024 * 1024;
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1.5);

        private static readonly ConcurrentDictionary<string, CapLiveUpload> Sessions = new ConcurrentDictionary<string, CapLiveUpload>(StringComparer.OrdinalIgnoreCase);

        private readonly HttpClient client = HttpClientFactory.Create();
        private readonly string server;
        private readonly string apiKey;
        private readonly string organizationId;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        private readonly List<CapUploadedPart> parts = new List<CapUploadedPart>();
        private string filePath;
        private string uploadId;
        private long position;
        private bool failed;
        private Task loop;

        public string VideoId { get; private set; }
        public string ShareURL => string.IsNullOrEmpty(VideoId) ? null : $"{server}/s/{VideoId}";

        private CapLiveUpload(string server, string apiKey, string organizationId, string filePath)
        {
            this.server = server;
            this.apiKey = apiKey;
            this.organizationId = organizationId;
            this.filePath = filePath;
        }

        public static CapLiveUpload Start(string serverURL, string apiKey, string organizationId, string filePath)
        {
            if (!Cap.TryGetServerOrigin(serverURL, out string server) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrEmpty(filePath))
            {
                return null;
            }

            CapLiveUpload session = new CapLiveUpload(server, apiKey.Trim(), organizationId, filePath);
            Sessions[Path.GetFullPath(filePath)] = session;
            session.loop = Task.Run(session.RunAsync);
            return session;
        }

        // Hands the session for a finished recording to whoever uploads the file, so it is completed
        // instead of being uploaded a second time.
        public static bool TryTake(string filePath, out CapLiveUpload session)
        {
            session = null;
            return !string.IsNullOrEmpty(filePath) && Sessions.TryRemove(Path.GetFullPath(filePath), out session);
        }

        public static void Rename(string oldFilePath, string newFilePath)
        {
            if (TryTake(oldFilePath, out CapLiveUpload session))
            {
                session.filePath = newFilePath;
                Sessions[Path.GetFullPath(newFilePath)] = session;
            }
        }

        // Drops the session for a recording that will not be uploaded after all and removes the
        // half-sent video from the server.
        public static void Abort(string filePath)
        {
            if (TryTake(filePath, out CapLiveUpload session))
            {
                _ = session.AbortAsync();
            }
        }

        private async Task RunAsync()
        {
            try
            {
                string query = "recordingMode=desktopMP4&name=" + Uri.EscapeDataString(Path.GetFileNameWithoutExtension(filePath));
                if (!string.IsNullOrWhiteSpace(organizationId)) query += "&orgId=" + Uri.EscapeDataString(organizationId.Trim());

                string createResponse = await SendAsync(System.Net.Http.HttpMethod.Get, "/api/desktop/video/create?" + query, null, cancellation.Token).ConfigureAwait(false);
                VideoId = JsonConvert.DeserializeObject<CapCreateVideoResponse>(createResponse)?.Id;

                string initiateResponse = await SendAsync(System.Net.Http.HttpMethod.Post, "/api/upload/multipart/initiate",
                    new { videoId = VideoId, contentType = "video/mp4" }, cancellation.Token).ConfigureAwait(false);
                uploadId = JsonConvert.DeserializeObject<CapInitiateResponse>(initiateResponse)?.UploadId;

                if (string.IsNullOrEmpty(VideoId) || string.IsNullOrEmpty(uploadId))
                {
                    failed = true;
                    return;
                }

                while (!cancellation.IsCancellationRequested)
                {
                    await Task.Delay(PollInterval, cancellation.Token).ConfigureAwait(false);
                    await UploadAvailablePartsAsync(false, cancellation.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                failed = true;
                DebugHelper.WriteException(e);
            }
        }

        private async Task UploadAvailablePartsAsync(bool final, CancellationToken cancellationToken)
        {
            if (!File.Exists(filePath)) return;

            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                using (FileStream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    while (true)
                    {
                        long remaining = stream.Length - position;
                        if (remaining <= 0 || (!final && remaining < PartSize)) break;

                        byte[] buffer = new byte[(int)Math.Min(PartSize, remaining)];
                        stream.Position = position;
                        await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);

                        int partNumber = parts.Count + 1;
                        string presignResponse = await SendAsync(System.Net.Http.HttpMethod.Post, "/api/upload/multipart/presign-part",
                            new { videoId = VideoId, uploadId, partNumber }, cancellationToken).ConfigureAwait(false);
                        string presignedUrl = JsonConvert.DeserializeObject<CapPresignPartResponse>(presignResponse)?.PresignedUrl;
                        if (string.IsNullOrEmpty(presignedUrl)) throw new InvalidOperationException("Cap did not return an upload address.");

                        using (ByteArrayContent content = new ByteArrayContent(buffer))
                        using (HttpResponseMessage response = await client.PutAsync(presignedUrl, content, cancellationToken).ConfigureAwait(false))
                        {
                            response.EnsureSuccessStatusCode();
                            string etag = response.Headers.ETag?.Tag?.Trim('"');
                            if (string.IsNullOrEmpty(etag)) throw new InvalidOperationException("Cap's storage did not confirm the part.");

                            parts.Add(new CapUploadedPart { PartNumber = partNumber, Etag = etag, Size = buffer.Length });
                        }

                        position += buffer.Length;
                    }
                }
            }
            finally
            {
                gate.Release();
            }
        }

        // Sends whatever is left of the finished file and closes the upload. Returns the share link,
        // or null when the live upload could not be completed and the file should be uploaded normally.
        public async Task<string> CompleteAsync(VideoInfo info, CancellationToken cancellationToken)
        {
            cancellation.Cancel();

            try
            {
                if (loop != null) await loop.ConfigureAwait(false);
                if (failed || string.IsNullOrEmpty(uploadId)) throw new InvalidOperationException("The live upload did not start.");

                await UploadAvailablePartsAsync(true, cancellationToken).ConfigureAwait(false);
                if (parts.Count == 0) throw new InvalidOperationException("The recording is empty.");

                Dictionary<string, object> complete = new Dictionary<string, object>
                {
                    ["videoId"] = VideoId,
                    ["uploadId"] = uploadId,
                    ["parts"] = parts
                };

                if (info != null)
                {
                    if (info.Duration > TimeSpan.Zero) complete["durationInSecs"] = info.Duration.TotalSeconds;
                    if (!info.VideoResolution.IsEmpty)
                    {
                        complete["width"] = info.VideoResolution.Width;
                        complete["height"] = info.VideoResolution.Height;
                    }
                    if (info.VideoFPS > 0) complete["fps"] = info.VideoFPS;
                }

                string response = await SendAsync(System.Net.Http.HttpMethod.Post, "/api/upload/multipart/complete", complete, cancellationToken).ConfigureAwait(false);
                if (JsonConvert.DeserializeObject<CapCompleteResponse>(response)?.Success != true) throw new InvalidOperationException("Cap did not confirm the upload.");

                return ShareURL;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await AbortAsync().ConfigureAwait(false);
                throw;
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
                await AbortAsync().ConfigureAwait(false);
                return null;
            }
        }

        public async Task AbortAsync()
        {
            cancellation.Cancel();

            try
            {
                if (loop != null) await loop.ConfigureAwait(false);

                if (!string.IsNullOrEmpty(uploadId))
                {
                    await SendAsync(System.Net.Http.HttpMethod.Post, "/api/upload/multipart/abort", new { videoId = VideoId, uploadId }, CancellationToken.None).ConfigureAwait(false);
                }

                if (!string.IsNullOrEmpty(VideoId))
                {
                    await SendAsync(System.Net.Http.HttpMethod.Delete, "/api/desktop/video/delete?videoId=" + Uri.EscapeDataString(VideoId), null, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
            }
        }

        private async Task<string> SendAsync(System.Net.Http.HttpMethod method, string path, object body, CancellationToken cancellationToken)
        {
            using (HttpRequestMessage request = new HttpRequestMessage(method, server + path))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                request.Headers.Accept.ParseAdd("application/json");
                if (body != null) request.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");

                using (HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }
}
