namespace TqkLibrary.Asp.ToolKit.Integration
{
    public interface IDownloadFile
    {
        /// <summary>
        /// full url or relative path
        /// </summary>
        /// <param name="url"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        Task<Stream> DownloadFileAsync(string url, CancellationToken cancellationToken = default);
    }
}
