namespace TqkLibrary.Asp.ToolKit.Integration
{
    public class HttpClientStreamWrapper : Stream
    {
        readonly HttpResponseMessage _httpResponseMessage;
        readonly Stream _stream;
        public HttpClientStreamWrapper(
            HttpResponseMessage httpResponseMessage,
            Stream stream
            )
        {
            this._httpResponseMessage = httpResponseMessage ?? throw new ArgumentNullException(nameof(httpResponseMessage));
            this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
        }
        protected override void Dispose(bool disposing)
        {
            _stream.Dispose();
            _httpResponseMessage.Dispose();
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            await _stream.DisposeAsync();
            _httpResponseMessage.Dispose();
            await base.DisposeAsync();
        }
        public override bool CanTimeout => _stream.CanTimeout;
        public override int ReadTimeout { get => _stream.ReadTimeout; set => _stream.ReadTimeout = value; }
        public override int WriteTimeout { get => _stream.WriteTimeout; set => _stream.WriteTimeout = value; }
        public override void CopyTo(Stream destination, int bufferSize) => _stream.CopyTo(destination, bufferSize);
        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
            => _stream.CopyToAsync(destination, bufferSize, cancellationToken);
        public override bool CanRead => _stream.CanRead;
        public override bool CanSeek => _stream.CanSeek;
        public override bool CanWrite => _stream.CanWrite;
        public override long Length => _httpResponseMessage.Content.Headers.ContentLength ?? _stream.Length;
        public override long Position { get => _stream.Position; set => _stream.Position = value; }
        public override void Flush() => _stream.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _stream.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => _stream.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => _stream.ReadAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => _stream.Seek(offset, origin);
        public override void SetLength(long value) => _stream.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _stream.Write(buffer, offset, count);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => _stream.WriteAsync(buffer, offset, count, cancellationToken);
        public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
            => _stream.BeginRead(buffer, offset, count, callback, state);
        public override int EndRead(IAsyncResult asyncResult)
            => _stream.EndRead(asyncResult);
        public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
            => _stream.BeginWrite(buffer, offset, count, callback, state);
        public override void EndWrite(IAsyncResult asyncResult)
            => _stream.EndWrite(asyncResult);
    }
}
