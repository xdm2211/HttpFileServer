using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace HttpFileServer.Handlers
{
    public abstract class HttpHandlerBase : IHttpHandler
    {
        #region Constructors

        public HttpHandlerBase(string rootDir)
        {
            SourceDir = rootDir;
        }

        #endregion Constructors

        #region Properties

        public bool EnableUpload { get; set; }

        public string SourceDir { get; private set; }

        #endregion Properties

        #region Methods

        public abstract Task ProcessRequest(HttpListenerContext context);

        protected static bool IsPathWithinRoot(string rootDir, string path)
        {
            if (string.IsNullOrWhiteSpace(rootDir) || string.IsNullOrWhiteSpace(path))
                return false;

            try
            {
                var fullRoot = Path.GetFullPath(rootDir);
                var fullPath = Path.GetFullPath(path);
                var rootPath = Path.GetPathRoot(fullRoot);

                if (!string.Equals(fullRoot, rootPath, StringComparison.OrdinalIgnoreCase))
                    fullRoot = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (string.Equals(fullRoot, rootPath, StringComparison.OrdinalIgnoreCase))
                    return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);

                return fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) ||
                       fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        protected static bool TryResolvePathWithinRoot(string rootDir, string requestPath, out string fullPath)
        {
            fullPath = null;
            if (string.IsNullOrWhiteSpace(rootDir))
                return false;

            try
            {
                if (HasEncodedPathSeparatorOrTraversal(requestPath))
                    return false;

                var relativePath = (requestPath ?? string.Empty)
                    .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                    .TrimStart(Path.DirectorySeparatorChar);
                var candidate = Path.GetFullPath(Path.Combine(Path.GetFullPath(rootDir), relativePath));
                if (!IsPathWithinRoot(rootDir, candidate))
                    return false;

                fullPath = candidate;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool HasEncodedPathSeparatorOrTraversal(string requestPath)
        {
            var probe = requestPath ?? string.Empty;
            for (var index = 0; index < 8; index++)
            {
                if (ContainsParentPathSegment(probe))
                    return true;

                var decoded = Uri.UnescapeDataString(probe);
                if (string.Equals(decoded, probe, StringComparison.Ordinal))
                    return false;

                if (CountPathSeparators(decoded) > CountPathSeparators(probe))
                    return true;

                probe = decoded;
            }

            // Excessive encoding layers are not valid request paths for this server.
            return true;
        }

        private static bool ContainsParentPathSegment(string path)
        {
            return (path ?? string.Empty)
                .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment == "..");
        }

        private static int CountPathSeparators(string path)
        {
            return (path ?? string.Empty).Count(character =>
                character == Path.DirectorySeparatorChar || character == Path.AltDirectorySeparatorChar);
        }

        #endregion Methods
    }
}
