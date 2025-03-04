using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using vtortola.WebSockets.Rfc6455.Header;
using vtortola.WebSockets.Tools;
using vtortola.WebSockets.Transports;

namespace vtortola.WebSockets.Rfc6455
{

    internal partial class WebSocketConnectionRfc6455 : IDisposable
    {
        [Flags]
        internal enum SendOptions { None, NoLock = 0x1, NoErrors = 0x2, IgnoreClose = 0x4 }

        private const int WS_STATE_OPEN = 0;
        private const int WS_STATE_CLOSE_SENT = 1;
        private const int WS_STATE_CLOSE_RECEIVED = 2;
        private const int WS_STATE_CLOSED = 3;
        private const int WS_STATE_DISPOSED = 4;

        private readonly ArraySegment<byte> headerBuffer, outPingBuffer, outPongBuffer, outCloseBuffer, inPingBuffer, inPongBuffer, inCloseBuffer;
        internal readonly ArraySegment<byte> SendBuffer;

        private readonly ILogger log;
        private readonly SemaphoreSlim writeSemaphore;
        private readonly NetworkConnection networkConnection;
        private readonly WebSocketListenerOptions options;
        private readonly PingHandler pingHandler;
        private readonly bool maskData;
        public volatile WebSocketFrameHeader CurrentHeader;
        public WebSocketCloseReason? CloseReason;
        private volatile int ongoingMessageWrite, ongoingMessageAwaiting, closeState;
        private TimeSpan latency;

        public ILogger Log => this.log;

        public bool CanReceive => this.closeState == WS_STATE_OPEN || this.closeState == WS_STATE_CLOSE_SENT;
        public bool CanSend => this.closeState == WS_STATE_OPEN || this.closeState == WS_STATE_CLOSE_RECEIVED;
        public bool IsClosed => this.closeState >= WS_STATE_CLOSED;

        public TimeSpan Latency
        {
            get
            {
                if (this.options.PingMode != PingMode.LatencyControl)
                    throw new InvalidOperationException("PingMode has not been set to 'LatencyControl', so latency is not available");
                return this.latency;
            }
        }
        public WebSocketListenerOptions Options => this.options;

        public WebSocketConnectionRfc6455([NotNull] NetworkConnection networkConnection, bool maskData, [NotNull] WebSocketListenerOptions options)
        {
            if (networkConnection == null) throw new ArgumentNullException(nameof(networkConnection));
            if (options == null) throw new ArgumentNullException(nameof(options));

            const int HEADER_SEGMENT_SIZE = 16;
            const int PONG_SEGMENT_SIZE = 128;
            const int PING_SEGMENT_SIZE = 128;
            const int CLOSE_SEGMENT_SIZE = 2;

            this.log = options.Logger;

            this.writeSemaphore = new SemaphoreSlim(1);
            this.options = options;

            this.networkConnection = networkConnection;
            this.maskData = maskData;

            var bufferSize = HEADER_SEGMENT_SIZE +
                HEADER_SEGMENT_SIZE + PONG_SEGMENT_SIZE +
                HEADER_SEGMENT_SIZE + PING_SEGMENT_SIZE +
                HEADER_SEGMENT_SIZE + PONG_SEGMENT_SIZE +
                HEADER_SEGMENT_SIZE + PING_SEGMENT_SIZE +
                CLOSE_SEGMENT_SIZE;

            var smallBuffer = this.options.BufferManager.TakeBuffer(bufferSize);
            this.headerBuffer = new ArraySegment<byte>(smallBuffer, 0, HEADER_SEGMENT_SIZE);
            this.outPongBuffer = this.headerBuffer.NextSegment(HEADER_SEGMENT_SIZE).NextSegment(PONG_SEGMENT_SIZE);
            this.outPingBuffer = this.outPongBuffer.NextSegment(HEADER_SEGMENT_SIZE).NextSegment(PING_SEGMENT_SIZE);
            this.outCloseBuffer = this.outPingBuffer.NextSegment(HEADER_SEGMENT_SIZE).NextSegment(CLOSE_SEGMENT_SIZE);
            this.inPongBuffer = this.outCloseBuffer.NextSegment(HEADER_SEGMENT_SIZE).NextSegment(PONG_SEGMENT_SIZE);
            this.inPingBuffer = this.inPongBuffer.NextSegment(HEADER_SEGMENT_SIZE).NextSegment(PING_SEGMENT_SIZE);
            this.inCloseBuffer = this.inPingBuffer.NextSegment(HEADER_SEGMENT_SIZE).NextSegment(CLOSE_SEGMENT_SIZE);

            var sendBuffer = this.options.BufferManager.TakeBuffer(this.options.SendBufferSize);
            this.SendBuffer = new ArraySegment<byte>(sendBuffer, HEADER_SEGMENT_SIZE, sendBuffer.Length - HEADER_SEGMENT_SIZE);

            switch (options.PingMode)
            {
                case PingMode.BandwidthSaving:
                    this.pingHandler = new BandwidthSavingPing(this);
                    break;
                case PingMode.LatencyControl:
                    this.pingHandler = new LatencyControlPing(this);
                    break;
                case PingMode.Manual:
                    this.pingHandler = new ManualPing(this);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown value '{options.PingMode}' for '{nameof(PingMode)}' enumeration.");
            }
        }

        private void CheckForDoubleRead()
        {
            if (Interlocked.CompareExchange(ref this.ongoingMessageAwaiting, 1, 0) == 1)
                throw new WebSocketException("There is an ongoing message await from somewhere else. Only a single write is allowed at the time.");

            if (this.CurrentHeader != null)
                throw new WebSocketException("There is an ongoing message that is being read from somewhere else.");
        }
        public async Task AwaitHeaderAsync(CancellationToken cancellation)
        {
            this.CheckForDoubleRead();
            try
            {
                while (this.CanReceive && this.CurrentHeader == null)
                {
                    var buffered = 0;
                    var estimatedHeaderLength = 2;
                    // try read minimal frame first
                    while (buffered < estimatedHeaderLength && !cancellation.IsCancellationRequested)
                    {
                        var read = await this.networkConnection.ReadAsync(this.headerBuffer.Array, this.headerBuffer.Offset + buffered, estimatedHeaderLength - buffered, cancellation).ConfigureAwait(false);
                        if (read == 0)
                        {
                            buffered = 0;
                            break;
                        }

                        buffered += read;
                        if (buffered >= 2)
                            estimatedHeaderLength = WebSocketFrameHeader.GetHeaderLength(this.headerBuffer.Array, this.headerBuffer.Offset);
                    }

                    if (buffered == 0 || cancellation.IsCancellationRequested)
                    {
                        if (buffered == 0)
                        {
                            if (this.log.IsDebugEnabled)
                                this.log.Debug($"({this.GetHashCode():X}) Connection has been closed while async awaiting header.");
                        }
                        await this.CloseAsync(WebSocketCloseReason.ProtocolError).ConfigureAwait(false);
                        return;
                    }

                    await this.ParseHeaderAsync(buffered).ConfigureAwait(false);
                }
            }
            catch (Exception awaitHeaderError) when (awaitHeaderError.Unwrap() is ThreadAbortException == false)
            {
                if (this.IsClosed)
                    return;

                var awaitHeaderErrorUnwrap = awaitHeaderError.Unwrap();
                if (this.log.IsDebugEnabled && awaitHeaderErrorUnwrap is OperationCanceledException == false)
                    this.log.Debug($"({this.GetHashCode():X}) An error occurred while async awaiting header. Connection will be closed with 'Protocol Error' code.", awaitHeaderErrorUnwrap);

                await this.CloseAsync(WebSocketCloseReason.ProtocolError).ConfigureAwait(false);

                if (awaitHeaderErrorUnwrap is WebSocketException == false && awaitHeaderErrorUnwrap is OperationCanceledException == false)
                    throw new WebSocketException("Read operation on WebSocket stream is failed. More detailed information in inner exception.", awaitHeaderErrorUnwrap);
                else
                    throw;
            }
        }
        public void DisposeCurrentHeaderIfFinished()
        {
            if (this.CurrentHeader != null && this.CurrentHeader.RemainingBytes < 0)
            {
                throw new InvalidOperationException("Extra bytes are read from transport connection. Report to package developer about it.");
            }

            if (this.CurrentHeader != null && this.CurrentHeader.RemainingBytes == 0)
            {
                this.CurrentHeader = null;
            }
        }
        public async Task<int> ReceiveAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || offset > buffer.Length) throw new ArgumentOutOfRangeException(nameof(offset));
            if (count < 0 || offset + count > buffer.Length) throw new ArgumentOutOfRangeException(nameof(count));

            try
            {
                var read = await this.networkConnection.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
                this.CurrentHeader.DecodeBytes(buffer, offset, read);
                return read;
            }
            catch (Exception readError) when (readError.Unwrap() is ThreadAbortException == false)
            {
                var readErrorUnwrap = readError.Unwrap();
                if (this.log.IsDebugEnabled && readErrorUnwrap is OperationCanceledException == false && !this.IsClosed)
                    this.log.Debug($"({this.GetHashCode():X}) An error occurred while async reading from WebSocket. Connection will be closed with 'Unexpected Condition' code.", readErrorUnwrap);

                await this.CloseAsync(WebSocketCloseReason.UnexpectedCondition).ConfigureAwait(false);

                if (readErrorUnwrap is WebSocketException == false && readErrorUnwrap is OperationCanceledException == false)
                    throw new WebSocketException("Read operation on WebSocket stream is failed: " + readErrorUnwrap.Message, readErrorUnwrap);
                else
                    throw;
            }
        }

        public void EndWriting()
        {
            this.ongoingMessageWrite = 0;
        }
        public void BeginWriting()
        {
            if (Interlocked.CompareExchange(ref this.ongoingMessageWrite, 1, 0) == 1)
                throw new WebSocketException("There is an ongoing message that is being written from somewhere else. Only a single write is allowed at the time.");
        }

        public ArraySegment<byte> PrepareFrame(ArraySegment<byte> payload, int length, bool isCompleted, bool headerSent, WebSocketMessageType type, WebSocketExtensionFlags extensionFlags)
        {
            var mask = 0U;
            if (this.maskData)
                mask = unchecked((uint)ThreadStaticRandom.NextNotZero());

            var header = WebSocketFrameHeader.Create(length, isCompleted, headerSent, mask, (WebSocketFrameOption)type, extensionFlags);
            if (header.WriteTo(payload.Array, payload.Offset - header.HeaderLength) != header.HeaderLength)
                throw new WebSocketException("Wrong frame header written.");

            if (this.log.IsDebugEnabled)
                this.log.Debug($"({this.GetHashCode():X}) [FRAME->] {header}");

            header.EncodeBytes(payload.Array, payload.Offset, length);

            return new ArraySegment<byte>(payload.Array, payload.Offset - header.HeaderLength, length + header.HeaderLength);
        }

        public Task<bool> SendFrameAsync(ArraySegment<byte> frame, CancellationToken cancellation)
        {
            return this.SendFrameAsync(frame, Timeout.InfiniteTimeSpan, SendOptions.None, cancellation);
        }

        private async Task<bool> SendFrameAsync(ArraySegment<byte> frame, TimeSpan timeout, SendOptions sendOptions, CancellationToken cancellation)
        {
            var noLock = (sendOptions & SendOptions.NoLock) == SendOptions.NoLock;
            var noError = (sendOptions & SendOptions.NoErrors) == SendOptions.NoErrors;
            var ignoreClose = (sendOptions & SendOptions.IgnoreClose) == SendOptions.IgnoreClose;

            try
            {
                if (!ignoreClose && this.IsClosed)
                {
                    if (noError)
                        return false;

                    throw new WebSocketException("WebSocket connection is closed.");
                }

                var lockTaken = noLock || await this.writeSemaphore.WaitAsync(timeout, cancellation).ConfigureAwait(false);
                try
                {
                    if (!lockTaken)
                    {
                        if (noError)
                            return false;

                        throw new WebSocketException($"Write operation lock timeout ({timeout.TotalMilliseconds:F2}ms).");
                    }

                    await this.networkConnection.WriteAsync(frame.Array, frame.Offset, frame.Count, cancellation).ConfigureAwait(false);

                    return true;
                }
                finally
                {
                    if (lockTaken && !noLock)
                        SafeEnd.ReleaseSemaphore(this.writeSemaphore, this.log);
                }
            }
            catch (Exception writeError) when (writeError.Unwrap() is ThreadAbortException == false)
            {
                if (noError)
                    return false;

                var writeErrorUnwrap = writeError.Unwrap();
                if (writeErrorUnwrap is ObjectDisposedException)
                    writeErrorUnwrap = new IOException("Network connection has been closed.", writeErrorUnwrap);

                if (this.log.IsDebugEnabled && writeErrorUnwrap is OperationCanceledException == false && !this.IsClosed)
                    this.log.Debug($"({this.GetHashCode():X}) Write operation on WebSocket stream is failed. Connection will be closed with 'Unexpected Condition' code.", writeErrorUnwrap);

                await this.CloseAsync(WebSocketCloseReason.UnexpectedCondition).ConfigureAwait(false);

                if (writeErrorUnwrap is WebSocketException == false && writeErrorUnwrap is OperationCanceledException == false)
                    throw new WebSocketException("Write operation on WebSocket stream is failed: " + writeErrorUnwrap.Message, writeErrorUnwrap);
                else
                    throw;
            }
        }

        private async Task ParseHeaderAsync(int read)
        {
            if (read < 2)
            {
                if (this.log.IsWarningEnabled)
                    this.log.Warning($"{nameof(this.ParseHeaderAsync)} is called with only {read} bytes buffer. Minimal is 2 bytes.");

                await this.CloseAsync(WebSocketCloseReason.ProtocolError).ConfigureAwait(false);
                return;
            }

            var headerLength = WebSocketFrameHeader.GetHeaderLength(this.headerBuffer.Array, this.headerBuffer.Offset);

            if (read != headerLength)
            {
                if (this.log.IsWarningEnabled)
                    this.log.Warning($"{nameof(this.ParseHeaderAsync)} is called with {read} bytes buffer. While whole header is {headerLength} bytes length. Connection will be closed with 'Protocol Error' code.");

                await this.CloseAsync(WebSocketCloseReason.ProtocolError).ConfigureAwait(false);
                return;
            }

            WebSocketFrameHeader header;
            if (!WebSocketFrameHeader.TryParse(this.headerBuffer.Array, this.headerBuffer.Offset, headerLength, out header))
                throw new WebSocketException("Frame header is malformed.");

            if (this.log.IsDebugEnabled)
                this.log.Debug($"({this.GetHashCode():X}) [FRAME<-] {header}");

            this.CurrentHeader = header;

            if (!header.Flags.Option.IsData())
            {
                await this.ProcessControlFrameAsync().ConfigureAwait(false);
                this.CurrentHeader = null;
            }
            else
            {
                this.ongoingMessageAwaiting = 0;
            }

            try
            {
                this.pingHandler.NotifyActivity();
            }
            catch (Exception notifyPingError)
            {
                if (this.log.IsWarningEnabled)
                    this.log.Warning($"({this.GetHashCode():X}) An error occurred while trying to call {this.pingHandler.GetType().Name}.{nameof(this.pingHandler.NotifyActivity)}() method.", notifyPingError);
            }
        }
        private async Task ProcessControlFrameAsync()
        {
            switch (this.CurrentHeader.Flags.Option)
            {
                case WebSocketFrameOption.Continuation:
                case WebSocketFrameOption.Text:
                case WebSocketFrameOption.Binary:
                    throw new WebSocketException("Text, Continuation or Binary are not protocol frames");

                case WebSocketFrameOption.ConnectionClose:
                    var closePayloadToRead = Math.Min(2, (int)this.CurrentHeader.ContentLength);
                    var closeMessageOffset = 0;
                    while (closeMessageOffset < closePayloadToRead)
                    {
                        var closeBytesRead = await this.networkConnection.ReadAsync
                        (
                            this.inCloseBuffer.Array,
                            this.inCloseBuffer.Offset + closeMessageOffset,
                            closePayloadToRead - closeMessageOffset,
                            CancellationToken.None
                        ).ConfigureAwait(false);

                        if (closeBytesRead == 0)
                        {
                            break; // connection closed, no more data
                        }

                        closeMessageOffset += closeBytesRead;
                    }

                    if (closeMessageOffset >= closePayloadToRead && closePayloadToRead >= 2)
                    {
                        this.CurrentHeader.DecodeBytes(this.inCloseBuffer.Array, this.inCloseBuffer.Offset, 2);

                        this.CloseReason = (WebSocketCloseReason)EndianBitConverter.ToUInt16Be(this.inCloseBuffer.Array, this.inCloseBuffer.Offset);
                    }
                    else
                    {
                        this.CloseReason = WebSocketCloseReason.NormalClose;

                        EndianBitConverter.UInt16CopyBytesBe((ushort)this.CloseReason, this.inCloseBuffer.Array, this.inCloseBuffer.Offset);
                    }

                    if (Interlocked.CompareExchange(ref this.closeState, WS_STATE_CLOSE_RECEIVED, WS_STATE_OPEN) == WS_STATE_OPEN)
                    {
                        if (this.log.IsDebugEnabled)
                            this.log.Debug("A close frame is received while websocket in 'Open' state. Switching state to 'CloseReceived'.");
                    }
                    if (Interlocked.CompareExchange(ref this.closeState, WS_STATE_CLOSED, WS_STATE_CLOSE_SENT) == WS_STATE_CLOSE_SENT)
                    {
                        if (this.log.IsDebugEnabled)
                            this.log.Debug("A close frame is received while websocket in 'CloseSent' state. Switching state to 'Closed'.");

                        await this.networkConnection.CloseAsync().ConfigureAwait(false); // Possible RC with CloseAsync() cause premature close without sending our close frame
                    }
                    break;
                case WebSocketFrameOption.Ping:
                case WebSocketFrameOption.Pong:
                    var contentLength = this.inPongBuffer.Count;
                    if (this.CurrentHeader.ContentLength < 125)
                        contentLength = (int)this.CurrentHeader.ContentLength;

                    var isPong = this.CurrentHeader.Flags.Option == WebSocketFrameOption.Pong;
                    var buffer = isPong ? this.inPongBuffer : this.inPingBuffer;
                    var read = 0;
                    var totalRead = 0;
                    while (totalRead < contentLength)
                    {
                        read = await this.networkConnection.ReadAsync(buffer.Array, buffer.Offset + read, contentLength - read, CancellationToken.None).ConfigureAwait(false);
                        totalRead += read;
                    }
                    this.CurrentHeader.DecodeBytes(buffer.Array, buffer.Offset, contentLength);

                    if (isPong)
                    {
                        try
                        {
                            this.pingHandler.NotifyPong(buffer);
                        }
                        catch (Exception notifyPong)
                        {
                            if (this.log.IsWarningEnabled)
                                this.log.Warning($"({this.GetHashCode():X}) An error occurred while trying to call {this.pingHandler.GetType().Name}.{nameof(this.pingHandler.NotifyPong)}() method.", notifyPong);
                        }
                    }
                    else // pong frames echo what was 'pinged'
                    {
                        Buffer.BlockCopy(buffer.Array, buffer.Offset, this.outPongBuffer.Array, this.outPongBuffer.Offset, totalRead);

                        var frame = this.PrepareFrame(this.outPongBuffer, totalRead, true, false, (WebSocketMessageType)WebSocketFrameOption.Pong, WebSocketExtensionFlags.None);
                        await this.SendFrameAsync(frame, Timeout.InfiniteTimeSpan, SendOptions.NoErrors, CancellationToken.None).ConfigureAwait(false);
                    }

                    break;
                default:
                    throw new WebSocketException($"Unexpected header option '{this.CurrentHeader.Flags.Option}'");
            }
        }

        public async Task CloseAsync(WebSocketCloseReason reason)
        {
            if (Interlocked.CompareExchange(ref this.closeState, WS_STATE_CLOSE_SENT, WS_STATE_OPEN) != WS_STATE_OPEN &&
                Interlocked.CompareExchange(ref this.closeState, WS_STATE_CLOSED, WS_STATE_CLOSE_RECEIVED) != WS_STATE_CLOSE_RECEIVED)
            {
                return;
            }

            if (this.log.IsDebugEnabled)
                this.log.Debug($"A close is called on websocket. Switching state to '{(this.closeState == WS_STATE_CLOSE_SENT ? "CloseSent" : "Closed")}'.");

            await this.writeSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                EndianBitConverter.UInt16CopyBytesBe((ushort)reason, this.outCloseBuffer.Array, this.outCloseBuffer.Offset);
                var messageType = (WebSocketMessageType)WebSocketFrameOption.ConnectionClose;
                var closeFrame = this.PrepareFrame(this.outCloseBuffer, 2, true, false, messageType, WebSocketExtensionFlags.None);

                await this.SendFrameAsync(closeFrame, Timeout.InfiniteTimeSpan, SendOptions.NoLock | SendOptions.NoErrors | SendOptions.IgnoreClose, CancellationToken.None).ConfigureAwait(false);
                await this.networkConnection.FlushAsync(CancellationToken.None).ConfigureAwait(false);

                if (this.closeState >= WS_STATE_CLOSED)
                {
                    await this.networkConnection.CloseAsync().ConfigureAwait(false);
                }
            }
            catch (Exception closeError) when (closeError.Unwrap() is ThreadAbortException == false)
            {
                var closeErrorUnwrap = closeError.Unwrap();
                if (closeErrorUnwrap is IOException || closeErrorUnwrap is OperationCanceledException || closeErrorUnwrap is InvalidOperationException)
                    return; // ignore common IO exceptions while closing connection

                if (this.log.IsDebugEnabled)
                    this.log.Debug($"({this.GetHashCode():X}) An error occurred while closing connection.", closeError.Unwrap());
            }
            finally
            {
                SafeEnd.ReleaseSemaphore(this.writeSemaphore, this.log);
            }
        }
        public Task PingAsync(byte[] data, int offset, int count)
        {
            if (!this.CanSend)
                return TaskHelper.CompletedTask;

            if (this.pingHandler is ManualPing)
            {
                if (data != null)
                {
                    if (offset < 0 || offset > data.Length) throw new ArgumentOutOfRangeException(nameof(offset));
                    if (count < 0 || count > 125 || offset + count > data.Length) throw new ArgumentOutOfRangeException(nameof(count));

                    this.outPingBuffer.Array[this.outPingBuffer.Offset] = (byte)count;
                    Buffer.BlockCopy(data, offset, this.outPingBuffer.Array, this.outPingBuffer.Offset + 1, count);
                }
                else
                {
                    this.outPingBuffer.Array[this.outPingBuffer.Offset] = 0;
                }
            }

            return this.pingHandler.PingAsync();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref this.closeState, WS_STATE_DISPOSED) == WS_STATE_DISPOSED)
                return;

            this.latency = Timeout.InfiniteTimeSpan;

            SafeEnd.Dispose(this.networkConnection, this.log);
            SafeEnd.Dispose(this.writeSemaphore, this.log);

            this.options.BufferManager.ReturnBuffer(this.SendBuffer.Array);
            this.options.BufferManager.ReturnBuffer(this.headerBuffer.Array);
        }
    }

}
