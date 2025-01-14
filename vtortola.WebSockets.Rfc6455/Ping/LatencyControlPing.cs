using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using vtortola.WebSockets.Rfc6455.Header;

namespace vtortola.WebSockets.Rfc6455
{
    partial class WebSocketConnectionRfc6455
    {
        private sealed class LatencyControlPing : PingHandler
        {
            private readonly ArraySegment<byte> pingBuffer;
            private readonly TimeSpan pingTimeout;
            private readonly TimeSpan pingInterval;
            private readonly WebSocketConnectionRfc6455 connection;
            private TimeSpan lastActivityTime;

            public LatencyControlPing(WebSocketConnectionRfc6455 connection)
            {
                if (connection == null) throw new ArgumentNullException(nameof(connection));

                this.pingTimeout = connection.options.PingTimeout;
                if (this.pingTimeout < TimeSpan.Zero)
                    this.pingTimeout = TimeSpan.MaxValue;
                this.pingInterval = connection.options.PingInterval;

                this.pingBuffer = connection.outPingBuffer;
                this.connection = connection;
                this.NotifyActivity();
            }

            public override async Task PingAsync()
            {
                if (this.connection.IsClosed)
                    return;

                var elapsedTime = TimestampToTimeSpan(Stopwatch.GetTimestamp()) - this.lastActivityTime;
                if (elapsedTime > this.pingTimeout)
                {
                    this.connection.latency = Timeout.InfiniteTimeSpan;
                    SafeEnd.Dispose(this.connection);

                    if (this.connection.log.IsDebugEnabled)
                        this.connection.log.Debug($"WebSocket connection ({this.connection.GetHashCode():X}) has been closed due ping timeout. Time elapsed: {elapsedTime}, timeout: {this.pingTimeout}, interval: {this.pingInterval}.");

                    return;
                }

                EndianBitConverter.UInt64CopyBytesLe((ulong)Stopwatch.GetTimestamp(), this.pingBuffer.Array, this.pingBuffer.Offset);
                var messageType = (WebSocketMessageType)WebSocketFrameOption.Ping;

                var pingFrame = this.connection.PrepareFrame(this.pingBuffer, 8, true, false, messageType, WebSocketExtensionFlags.None);
                var pingFrameLockTimeout = elapsedTime < this.pingInterval ? TimeSpan.Zero : Timeout.InfiniteTimeSpan;

                //
                // ping_interval is 33% of ping_timeout time
                //
                // if elapsed_time < ping_interval then TRY to send ping frame
                // if elapsed_time > ping_interval then ENFORCE sending ping frame because ping_timeout is near
                //
                // pingFrameLockTimeout is controlling TRY/ENFORCE behaviour. Zero mean TRY to take write lock or skip. InfiniteTimeSpan mean wait indefinitely for write lock.
                //

                await this.connection.SendFrameAsync(pingFrame, pingFrameLockTimeout, SendOptions.NoErrors, CancellationToken.None).ConfigureAwait(false);
            }

            /// <inheritdoc />
            public override void NotifyActivity()
            {
                this.lastActivityTime = TimestampToTimeSpan(Stopwatch.GetTimestamp());
            }
            public override void NotifyPong(ArraySegment<byte> pongBuffer)
            {
                this.NotifyActivity();

                var timeDelta = TimestampToTimeSpan(Stopwatch.GetTimestamp() - (long)EndianBitConverter.ToUInt64Le(pongBuffer.Array, pongBuffer.Offset));
                this.connection.latency = TimeSpan.FromMilliseconds(Math.Max(0, timeDelta.TotalMilliseconds / 2));
            }
        }
    }
}
