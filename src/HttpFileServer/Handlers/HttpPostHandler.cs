using System.Net;
using System.Threading.Tasks;

namespace HttpFileServer.Handlers
{
    public class HttpPostHandler : HttpHandlerBase
    {
        #region Fields

        private HttpPostFileHandler _postFileHandler;

        #endregion Fields

        #region Constructors

        public HttpPostHandler(string rootDir, long maxUploadSizeBytes = 0, string allowedUploadExtensions = null, long minimumFreeDiskSpaceBytes = 0) : base(rootDir)
        {
            _postFileHandler = new HttpPostFileHandler(rootDir, maxUploadSizeBytes, allowedUploadExtensions, minimumFreeDiskSpaceBytes);
        }

        #endregion Constructors

        #region Methods

        public override async Task ProcessRequest(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;
            if (context.Request.HttpMethod.ToUpper() != "POST")
                return;

            await _postFileHandler.ProcessRequest(context);
        }

        #endregion Methods
    }
}
