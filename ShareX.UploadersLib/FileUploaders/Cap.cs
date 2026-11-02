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
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ShareX.UploadersLib.FileUploaders
{
    public class CapFileUploaderService : FileUploaderService
    {
        public override FileDestination EnumValue { get; } = FileDestination.Cap;

        public override bool CheckConfig(UploadersConfig config)
        {
            return Cap.TryGetServerOrigin(config.CapServerURL, out _) && !string.IsNullOrWhiteSpace(config.CapAPIKey);
        }

        public override GenericUploader CreateUploader(UploadersConfig config, TaskReferenceHelper taskInfo)
        {
            return new Cap(config.CapServerURL, config.CapAPIKey)
            {
                OrganizationId = config.CapOrganizationId
            };
        }
    }

    // Uploads finished MP4 recordings to a Cap server (cap.so or self-hosted) the same way Cap's desktop
    // app does: create the video, send it as an S3 multipart upload through presigned part URLs, then
    // complete it so it shows up in the dashboard with a share link.
    public sealed class Cap : FileUploader
    {
        // Cap's server recognises desktop API keys by their length (a UUID).
        public const int APIKeyLength = 36;

        private const int PartSize = 10 * 1024 * 1024;

        public string ServerURL { get; }
        public string APIKey { get; }
        public string OrganizationId { get; set; }

        public Cap(string serverURL, string apiKey)
        {
            ServerURL = serverURL;
            APIKey = apiKey;
        }

        public static bool TryGetServerOrigin(string url, out string origin)
        {
            origin = null;

            if (Uri.TryCreate(url?.Trim(), UriKind.Absolute, out Uri uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                origin = uri.GetLeftPart(UriPartial.Authority);
                return true;
            }

            return false;
        }

        protected override async Task<UploadResult> UploadCoreAsync(Stream stream, string fileName, CancellationToken cancellationToken)
        {
            if (!TryGetServerOrigin(ServerURL, out string server))
            {
                Errors.Add("Cap server URL is not valid.");
                return null;
            }

            if (!Path.GetExtension(fileName).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
            {
                Errors.Add("Cap only accepts MP4 videos. Record as MP4, or pick another destination for this file type.");
                return null;
            }

            NameValueCollection headers = new NameValueCollection
            {
                ["Authorization"] = "Bearer " + APIKey?.Trim(),
                ["Accept"] = "application/json"
            };

            string filePath = (stream as FileStream)?.Name;
            VideoInfo info = ProbeVideo(filePath);

            // A recording that was streamed to Cap while it was being made only needs its tail sent.
            if (CapLiveUpload.TryTake(filePath, out CapLiveUpload live))
            {
                string liveURL = await live.CompleteAsync(info, cancellationToken).ConfigureAwait(false);

                if (!string.IsNullOrEmpty(liveURL))
                {
                    await UploadThumbnailAsync(server, live.VideoId, filePath, headers, cancellationToken).ConfigureAwait(false);

                    return new UploadResult(liveURL)
                    {
                        IsSuccess = true,
                        URL = liveURL
                    };
                }
            }

            Dictionary<string, string> createArgs = new Dictionary<string, string>
            {
                ["recordingMode"] = "desktopMP4",
                ["name"] = Path.GetFileNameWithoutExtension(fileName)
            };
            AddMetadata(createArgs, info);
            if (!string.IsNullOrWhiteSpace(OrganizationId)) createArgs["orgId"] = OrganizationId.Trim();

            string createResponse = await SendRequestAsync(HttpMethod.GET, server + "/api/desktop/video/create", createArgs, headers,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            string videoId = Deserialize<CapCreateVideoResponse>(createResponse)?.Id;

            if (string.IsNullOrEmpty(videoId))
            {
                Errors.Add("Cap did not create the video. Check the server URL and sign in again.");
                return null;
            }

            string initiateResponse = await PostJsonAsync(server + "/api/upload/multipart/initiate",
                new { videoId, contentType = "video/mp4" }, headers, cancellationToken).ConfigureAwait(false);
            string uploadId = Deserialize<CapInitiateResponse>(initiateResponse)?.UploadId;

            if (string.IsNullOrEmpty(uploadId))
            {
                Errors.Add("Cap did not start the upload.");
                return null;
            }

            List<CapUploadedPart> parts = new List<CapUploadedPart>();
            long length = stream.Length;
            long position = 0;
            int partNumber = 1;

            while (position < length)
            {
                long size = Math.Min(PartSize, length - position);

                string presignResponse = await PostJsonAsync(server + "/api/upload/multipart/presign-part",
                    new { videoId, uploadId, partNumber }, headers, cancellationToken).ConfigureAwait(false);
                string presignedUrl = Deserialize<CapPresignPartResponse>(presignResponse)?.PresignedUrl;

                if (string.IsNullOrEmpty(presignedUrl))
                {
                    Errors.Add($"Cap did not return an upload address for part {partNumber}.");
                    return null;
                }

                // The part goes straight to the storage backend, so the Cap credentials stay out of it.
                ResponseInfo partResponse = await SendRequestForResponseAsync(HttpMethod.PUT, presignedUrl, stream, position, size,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                string etag = partResponse?.Headers?["ETag"]?.Trim('"');

                if (partResponse == null || !partResponse.IsSuccess || string.IsNullOrEmpty(etag))
                {
                    Errors.Add($"Uploading part {partNumber} to Cap's storage failed. The storage address the server hands out must be reachable from this computer.");
                    return null;
                }

                parts.Add(new CapUploadedPart { PartNumber = partNumber, Etag = etag, Size = size });
                position += size;
                partNumber++;
            }

            Dictionary<string, object> complete = new Dictionary<string, object>
            {
                ["videoId"] = videoId,
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

            string completeResponse = await PostJsonAsync(server + "/api/upload/multipart/complete", complete, headers, cancellationToken).ConfigureAwait(false);

            if (Deserialize<CapCompleteResponse>(completeResponse)?.Success != true)
            {
                Errors.Add("Cap did not confirm the upload.");
                return null;
            }

            await UploadThumbnailAsync(server, videoId, filePath, headers, cancellationToken).ConfigureAwait(false);

            return new UploadResult(completeResponse)
            {
                IsSuccess = true,
                URL = $"{server}/s/{videoId}"
            };
        }

        // Cap's dashboard shows a poster frame it expects the client to supply. A missing poster is
        // cosmetic, so nothing here is allowed to fail the upload.
        private async Task UploadThumbnailAsync(string server, string videoId, string filePath, NameValueCollection headers, CancellationToken cancellationToken)
        {
            string ffmpegPath = FileHelpers.GetAbsolutePath("ffmpeg.exe");
            if (string.IsNullOrEmpty(filePath) || !File.Exists(ffmpegPath)) return;

            string thumbnailPath = Path.Combine(Path.GetTempPath(), $"ShareX-cap-thumbnail-{Guid.NewGuid():N}.jpg");
            bool previousAllowReportProgress = AllowReportProgress;

            try
            {
                using (FFmpegCLIManager ffmpeg = new FFmpegCLIManager(ffmpegPath) { ShowError = false })
                {
                    ffmpeg.Run($"-i \"{filePath}\" -frames:v 1 -vf \"scale='min(1280,iw)':-2\" -q:v 4 -y \"{thumbnailPath}\"");
                }

                if (!File.Exists(thumbnailPath)) return;

                string signedResponse = await PostJsonAsync(server + "/api/upload/signed",
                    new { videoId, subpath = "screenshot/screen-capture.jpg", method = "put" }, headers, cancellationToken).ConfigureAwait(false);
                CapPresignedPutData put = Deserialize<CapSignedResponse>(signedResponse)?.PresignedPutData;
                if (string.IsNullOrEmpty(put?.Url)) return;

                NameValueCollection putHeaders = new NameValueCollection();
                string contentType = "image/jpeg";

                if (put.Headers != null)
                {
                    foreach (KeyValuePair<string, string> header in put.Headers)
                    {
                        if (header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) contentType = header.Value;
                        else putHeaders[header.Key] = header.Value;
                    }
                }

                AllowReportProgress = false;
                using (FileStream thumbnail = new FileStream(thumbnailPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    await SendRequestForResponseAsync(HttpMethod.PUT, put.Url, thumbnail, 0, thumbnail.Length, contentType, putHeaders,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
            }
            finally
            {
                AllowReportProgress = previousAllowReportProgress;
                FileHelpers.DeleteFile(thumbnailPath);
            }
        }

        private Task<string> PostJsonAsync(string url, object body, NameValueCollection headers, CancellationToken cancellationToken)
        {
            return SendRequestAsync(HttpMethod.POST, url, JsonConvert.SerializeObject(body), RequestHelpers.ContentTypeJSON,
                headers: headers, cancellationToken: cancellationToken);
        }

        private static VideoInfo ProbeVideo(string filePath)
        {
            string ffmpegPath = FileHelpers.GetAbsolutePath("ffmpeg.exe");
            if (string.IsNullOrEmpty(filePath) || !File.Exists(ffmpegPath)) return null;

            try
            {
                using (FFmpegCLIManager ffmpeg = new FFmpegCLIManager(ffmpegPath) { ShowError = false })
                {
                    return ffmpeg.GetVideoInfo(filePath);
                }
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e);
                return null;
            }
        }

        private static void AddMetadata(Dictionary<string, string> args, VideoInfo info)
        {
            if (info == null) return;

            if (info.Duration > TimeSpan.Zero) args["durationInSecs"] = info.Duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

            if (!info.VideoResolution.IsEmpty)
            {
                args["width"] = info.VideoResolution.Width.ToString(CultureInfo.InvariantCulture);
                args["height"] = info.VideoResolution.Height.ToString(CultureInfo.InvariantCulture);
            }

            if (info.VideoFPS > 0) args["fps"] = info.VideoFPS.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static T Deserialize<T>(string json) where T : class
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            try
            {
                return JsonConvert.DeserializeObject<T>(json);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        // Signs in through the browser the way Cap's desktop app does: the server mints an API key for the
        // signed-in user and hands it back by redirecting to a one-shot listener on this computer.
        public static async Task<string> RequestAPIKeyAsync(string serverURL, Action<string> openBrowser, CancellationToken cancellationToken = default)
        {
            if (!TryGetServerOrigin(serverURL, out string server))
            {
                throw new ArgumentException("Cap server URL is not valid.", nameof(serverURL));
            }

            int port = GetFreePort();
            using HttpListener listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();

            openBrowser($"{server}/api/desktop/session/request?type=api_key&port={port}&platform=web");

            using CancellationTokenRegistration registration = cancellationToken.Register(listener.Stop);

            try
            {
                while (true)
                {
                    HttpListenerContext context = await listener.GetContextAsync().ConfigureAwait(false);
                    string apiKey = context.Request.QueryString["api_key"];
                    bool received = !string.IsNullOrEmpty(apiKey);

                    byte[] page = Encoding.UTF8.GetBytes(received
                        ? "<html><body style=\"font-family:Inter,Segoe UI,sans-serif;text-align:center;padding-top:15vh\"><h2>ShareX is connected to Cap</h2><p>You can close this tab and go back to ShareX.</p></body></html>"
                        : "<html><body></body></html>");
                    context.Response.ContentType = "text/html; charset=utf-8";
                    context.Response.ContentLength64 = page.Length;
                    await context.Response.OutputStream.WriteAsync(page, cancellationToken).ConfigureAwait(false);
                    context.Response.Close();

                    if (received) return apiKey;
                }
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }

        private static int GetFreePort()
        {
            TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }
    }

    public sealed class CapCreateVideoResponse
    {
        [JsonProperty("id")]
        public string Id { get; set; }
    }

    public sealed class CapInitiateResponse
    {
        [JsonProperty("uploadId")]
        public string UploadId { get; set; }
    }

    public sealed class CapPresignPartResponse
    {
        [JsonProperty("presignedUrl")]
        public string PresignedUrl { get; set; }
    }

    public sealed class CapUploadedPart
    {
        [JsonProperty("partNumber")]
        public int PartNumber { get; set; }

        [JsonProperty("etag")]
        public string Etag { get; set; }

        [JsonProperty("size")]
        public long Size { get; set; }
    }

    public sealed class CapCompleteResponse
    {
        [JsonProperty("success")]
        public bool Success { get; set; }
    }

    public sealed class CapSignedResponse
    {
        [JsonProperty("presignedPutData")]
        public CapPresignedPutData PresignedPutData { get; set; }
    }

    public sealed class CapPresignedPutData
    {
        [JsonProperty("url")]
        public string Url { get; set; }

        [JsonProperty("headers")]
        public Dictionary<string, string> Headers { get; set; }
    }
}
