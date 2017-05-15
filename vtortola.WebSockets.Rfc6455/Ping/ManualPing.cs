﻿using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using vtortola.WebSockets.Tools;

namespace vtortola.WebSockets.Rfc6455
{
    partial class WebSocketConnectionRfc6455
    {
        private sealed class ManualPing : PingHandler
        {
            private readonly TimeSpan _pingTimeout;
            private readonly ArraySegment<byte> _pingBuffer;
            private readonly WebSocketConnectionRfc6455 _connection;
            private readonly Stopwatch _lastPong;

            public ManualPing(WebSocketConnectionRfc6455 connection)
            {
                if (connection == null) throw new ArgumentNullException(nameof(connection));

                _pingTimeout = connection._options.PingTimeout < TimeSpan.Zero ? TimeSpan.MaxValue : connection._options.PingTimeout;
                _pingBuffer = connection._pingBuffer;
                _connection = connection;
                _lastPong = new Stopwatch();

            }

            public override async Task PingAsync()
            {
                if (this._lastPong.Elapsed > this._pingTimeout)
                {
                    await this._connection.CloseAsync(WebSocketCloseReasons.GoingAway).ConfigureAwait(false);
                    return;
                }

                var messageType = (WebSocketMessageType)WebSocketFrameOption.Ping;
                var count = this._pingBuffer.Array[this._pingBuffer.Offset];
                var payload = this._pingBuffer.Skip(1);

                var pingFrame = _connection.PrepareFrame(payload, count, true, false, messageType, WebSocketExtensionFlags.None);
                await _connection.SendFrameAsync(pingFrame, CancellationToken.None).ConfigureAwait(false);
            }
            /// <inheritdoc />
            public override void NotifyActivity()
            {

            }
            public override void NotifyPong(ArraySegment<byte> pongBuffer)
            {
                this._lastPong.Stop();
            }
        }
    }
}
