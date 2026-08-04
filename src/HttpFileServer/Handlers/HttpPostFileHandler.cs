using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace HttpFileServer.Handlers
{
    public class HttpPostFileHandler : HttpHandlerBase
    {
        private readonly HashSet<string> _allowedExtensions;
        private readonly long _maxUploadSizeBytes;
        private readonly long _minimumFreeDiskSpaceBytes;

        #region Constructors

        public HttpPostFileHandler(string rootDir, long maxUploadSizeBytes = 0, string allowedUploadExtensions = null, long minimumFreeDiskSpaceBytes = 0) : base(rootDir)
        {
            _maxUploadSizeBytes = Math.Max(0, maxUploadSizeBytes);
            _minimumFreeDiskSpaceBytes = Math.Max(0, minimumFreeDiskSpaceBytes);
            _allowedExtensions = new HashSet<string>(
                (allowedUploadExtensions ?? string.Empty)
                    .Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(extension => extension.Trim())
                    .Where(extension => extension.Length > 0)
                    .Select(extension => extension.StartsWith(".") ? extension : "." + extension),
                StringComparer.OrdinalIgnoreCase);
        }

        #endregion Constructors

        #region Methods

        public override async Task ProcessRequest(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;
            // CORS preflight
            if (request.HttpMethod.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                response.AddHeader("Access-Control-Allow-Origin", "*");
                response.AddHeader("Access-Control-Allow-Methods", "POST,OPTIONS");
                response.AddHeader("Access-Control-Allow-Headers", "Content-Type,X-Requested-With");
                response.StatusCode = (int)HttpStatusCode.NoContent;
                return;
            }

            if (!request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            // POST URL 即为目标文件路径（由前端将文件名拼接到请求路径中）
            if (!TryResolvePathWithinRoot(SourceDir, request.Url.LocalPath, out var targetPath))
            {
                response.StatusCode = (int)HttpStatusCode.Forbidden;
                return;
            }
            var targetFileName = Path.GetFileName(targetPath);
            if (string.IsNullOrEmpty(targetFileName))
            {
                await WriteJsonResponseAsync(response, HttpStatusCode.BadRequest, new { ok = false, error = "Request URL must include the target file name." });
                return;
            }

            var extension = Path.GetExtension(targetFileName) ?? string.Empty;
            if (_allowedExtensions.Count > 0 && !_allowedExtensions.Contains(extension))
            {
                await WriteJsonResponseAsync(response, HttpStatusCode.UnsupportedMediaType, new { ok = false, error = "This file type is not allowed." });
                return;
            }

            if (_maxUploadSizeBytes > 0 && request.ContentLength64 > _maxUploadSizeBytes)
            {
                await WriteJsonResponseAsync(response, HttpStatusCode.RequestEntityTooLarge, new { ok = false, error = "The uploaded file exceeds the configured size limit." });
                return;
            }

            var targetDir = Path.GetDirectoryName(targetPath);
            if (request.ContentLength64 >= 0 &&
                !HasSufficientDiskSpace(targetDir, request.ContentLength64, _minimumFreeDiskSpaceBytes))
            {
                await WriteJsonResponseAsync(response, 507, new { ok = false, error = "The target disk does not have enough free space." });
                return;
            }

            try
            {
                if (!Directory.Exists(targetDir))
                    Directory.CreateDirectory(targetDir);
            }
            catch (Exception)
            {
                await WriteJsonResponseAsync(response, HttpStatusCode.InternalServerError, new { ok = false, error = "The upload directory could not be created." });
                return;
            }

            // Stream directly to a newly-created file. CreateNew prevents concurrent uploads
            // from racing into an overwrite of the same destination.
            string dstFile = null;
            long savedSize = 0;
            try
            {
                using (var fs = CreateUniqueFile(targetPath, out dstFile))
                {
                    await CopyToAsyncWithLimit(request.InputStream, fs, _maxUploadSizeBytes, dstFile, _minimumFreeDiskSpaceBytes);
                    savedSize = fs.Length;
                }

                var fullSourceDir = Path.GetFullPath(SourceDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var relativePath = dstFile.Substring(fullSourceDir.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                await WriteJsonResponseAsync(response, HttpStatusCode.OK, new { ok = true, files = new[] { new { name = targetFileName, size = savedSize, saved = true, finalPath = relativePath, contentType = request.ContentType } } });
            }
            catch (UploadTooLargeException)
            {
                try { if (!string.IsNullOrEmpty(dstFile) && File.Exists(dstFile)) File.Delete(dstFile); } catch { }
                await WriteJsonResponseAsync(response, HttpStatusCode.RequestEntityTooLarge, new { ok = false, error = "The uploaded file exceeds the configured size limit." });
            }
            catch (InsufficientStorageException)
            {
                try { if (!string.IsNullOrEmpty(dstFile) && File.Exists(dstFile)) File.Delete(dstFile); } catch { }
                await WriteJsonResponseAsync(response, 507, new { ok = false, error = "The target disk does not have enough free space." });
            }
            catch (Exception)
            {
                // 删除写了一半的文件，避免留下损坏的文件
                try { if (!string.IsNullOrEmpty(dstFile) && File.Exists(dstFile)) File.Delete(dstFile); } catch { }
                await WriteJsonResponseAsync(response, HttpStatusCode.InternalServerError, new { ok = false, error = "The upload failed." });
            }
        }

        private static async Task CopyToAsyncWithLimit(Stream source, Stream destination, long maxBytes, string destinationPath, long minimumFreeDiskSpaceBytes)
        {
            var buffer = new byte[81920];
            long totalBytes = 0;
            int bytesRead;
            while ((bytesRead = await source.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                totalBytes += bytesRead;
                if (maxBytes > 0 && totalBytes > maxBytes)
                    throw new UploadTooLargeException();

                if (!HasSufficientDiskSpace(Path.GetDirectoryName(destinationPath), bytesRead, minimumFreeDiskSpaceBytes))
                    throw new InsufficientStorageException();

                await destination.WriteAsync(buffer, 0, bytesRead);
            }
        }

        private static bool HasSufficientDiskSpace(string path, long bytesToWrite, long minimumFreeDiskSpaceBytes)
        {
            if (!TryGetAvailableFreeSpace(path, out var availableBytes))
                return true;

            if (availableBytes < minimumFreeDiskSpaceBytes)
                return false;

            return bytesToWrite <= availableBytes - minimumFreeDiskSpaceBytes;
        }

        private static bool TryGetAvailableFreeSpace(string path, out long availableBytes)
        {
            availableBytes = 0;
            try
            {
                var existingPath = path;
                while (!string.IsNullOrWhiteSpace(existingPath) && !Directory.Exists(existingPath))
                    existingPath = Path.GetDirectoryName(existingPath);

                if (string.IsNullOrWhiteSpace(existingPath) ||
                    !GetDiskFreeSpaceEx(existingPath, out var freeBytesAvailable, out _, out _))
                    return false;

                availableBytes = freeBytesAvailable > long.MaxValue ? long.MaxValue : (long)freeBytesAvailable;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetDiskFreeSpaceEx(
            string directoryName,
            out ulong freeBytesAvailable,
            out ulong totalNumberOfBytes,
            out ulong totalNumberOfFreeBytes);

        private static FileStream CreateUniqueFile(string path, out string finalPath)
        {
            var directory = Path.GetDirectoryName(path);
            var name = Path.GetFileNameWithoutExtension(path);
            var extension = Path.GetExtension(path);

            for (var index = 0; index < 10000; index++)
            {
                finalPath = index == 0 ? path : Path.Combine(directory, $"{name}({index}){extension}");
                try
                {
                    return new FileStream(finalPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                }
                catch (IOException)
                {
                    if (!File.Exists(finalPath))
                        throw;
                }
            }

            throw new IOException("No available upload file name could be allocated.");
        }

        private static async Task WriteJsonResponseAsync(HttpListenerResponse response, HttpStatusCode statusCode, object payload)
        {
            await WriteJsonResponseAsync(response, (int)statusCode, payload);
        }

        private static async Task WriteJsonResponseAsync(HttpListenerResponse response, int statusCode, object payload)
        {
            var json = JsonConvert.SerializeObject(payload);
            var buffer = Encoding.UTF8.GetBytes(json);
            response.StatusCode = statusCode;
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength64 = buffer.LongLength;
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
        }

        private sealed class UploadTooLargeException : Exception
        {
        }

        private sealed class InsufficientStorageException : Exception
        {
        }

        #endregion Methods
    }
}
