namespace Shane32.GraphQL.AspNetCore.WebSockets;

/// <summary>
/// Manages a WebSocket connection, dispatching messages to the specified <see cref="IOperationMessageReceiveStream"/>,
/// and sending messages requested by the <see cref="IOperationMessageSendStream"/> implementation.
/// <br/><br/>
/// The <see cref="ExecuteAsync(IOperationMessageReceiveStream)"/> method may only be executed once for each
/// instance. Awaiting the result will return once the WebSocket connection has been properly closed from both
/// ends, after all messages have been sent.
/// <br/><br/>
/// Calls to <see cref="IOperationMessageReceiveStream.OnMessageReceivedAsync(OperationMessage)"/> be awaited
/// before dispatching subsequent messages.
/// <br/><br/>
/// Calls to <see cref="CloseConnectionAsync()"/> and <see cref="SendMessageAsync(OperationMessage)"/> may be
/// called on multiple threads simultaneously. They are queued for delivery and sent in the order posted.
/// Messages posted after requesting the connection be closed will be discarded.
/// </summary>
public class WebSocketConnection : IOperationMessageSendStream
{
    private readonly WebSocket _webSocket;
    private readonly AsyncMessagePump<Message> _pump;
    private readonly IGraphQLSerializer _serializer;
    private readonly WebSocketWriterStream _stream;
    private readonly TaskCompletionSource<bool> _outputClosed = new();
    private readonly CancellationToken _cancellationToken;
    private readonly int _closeTimeoutMs;
    private int _executed;

    private static readonly TimeSpan _defaultDisconnectionTimeout = TimeSpan.FromSeconds(10);

    /// <inheritdoc/>
    public DateTime LastMessageSentAt { get; private set; } = DateTime.UtcNow;

    /// <summary>
    /// Initializes an instance with the specified parameters.
    /// </summary>
    public WebSocketConnection(WebSocket webSocket, IGraphQLSerializer serializer, WebSocketHandlerOptions options, CancellationToken cancellationToken)
    {
        if (options.DisconnectionTimeout.HasValue) {
            if (options.DisconnectionTimeout.Value.TotalMilliseconds < -1 || options.DisconnectionTimeout.Value.TotalMilliseconds > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(options) + "." + nameof(WebSocketHandlerOptions.DisconnectionTimeout));
        }
        _closeTimeoutMs = (int)(options.DisconnectionTimeout ?? _defaultDisconnectionTimeout).TotalMilliseconds;
        _webSocket = webSocket ?? throw new ArgumentNullException(nameof(webSocket));
        _stream = new(webSocket);
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _pump = new(SendMessageAsync);
        _cancellationToken = cancellationToken;
    }

    /// <summary>
    /// Listens to incoming messages on the WebSocket specified in the constructor,
    /// dispatching the messages to the specified <paramref name="operationMessageReceiveStream"/>.
    /// Returns or throws <see cref="OperationCanceledException"/> when the WebSocket connection is closed.
    /// </summary>
    public virtual async Task ExecuteAsync(IOperationMessageReceiveStream operationMessageReceiveStream)
    {
        if (operationMessageReceiveStream == null)
            throw new ArgumentNullException(nameof(operationMessageReceiveStream));
        if (Interlocked.Exchange(ref _executed, 1) == 1)
            throw new InvalidOperationException($"{nameof(ExecuteAsync)} may only be called once per instance.");
        try {
            // set up a buffer in case a message is longer than one block
            var receiveStream = new MemoryStream();
            // set up a 16KB data block
            byte[] buffer = new byte[16384];
            // prep a Memory instance pointing to the block
            var bufferMemory = new Memory<byte>(buffer);
            // read messages until an exception occurs, the cancellation token is signaled, or a 'close' message is received
            while (true) {
                var result = await _webSocket.ReceiveAsync(bufferMemory, _cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close) {
                    // prevent any more messages from being queued
                    operationMessageReceiveStream.Dispose();
                    // send a close request if none was sent yet
                    if (!_outputClosed.Task.IsCompleted) {
                        // queue the closure
                        _ = CloseConnectionAsync();
                        // wait until the close has been sent
                        var completedTask = Task.WhenAny(
                            _outputClosed.Task,
                            Task.Delay(_closeTimeoutMs, _cancellationToken));
                    }
                    // quit
                    return;
                }
                // if this is the last block terminating a message
                if (result.EndOfMessage) {
                    // if only one block of data was sent for this message
                    if (receiveStream.Length == 0) {
                        // if the message is empty, skip to the next message
                        if (result.Count == 0)
                            continue;
                        // read the message
                        var bufferStream = new MemoryStream(buffer, 0, result.Count, false);
                        var message = await _serializer.ReadAsync<OperationMessage>(bufferStream, _cancellationToken);
                        // dispatch the message
                        System.Diagnostics.Debug.WriteLine($"message rec'd: {message?.Type}");
                        if (message != null)
                            await operationMessageReceiveStream.OnMessageReceivedAsync(message);
                    } else {
                        // if there is any data in this block, add it to the buffer
                        if (result.Count > 0)
                            receiveStream.Write(buffer, 0, result.Count);
                        // read the message from the buffer
                        receiveStream.Position = 0;
                        var message = await _serializer.ReadAsync<OperationMessage>(receiveStream, _cancellationToken);
                        // clear the buffer
                        receiveStream.SetLength(0);
                        // dispatch the message
                        if (message != null)
                            await operationMessageReceiveStream.OnMessageReceivedAsync(message);
                    }
                } else {
                    // if there is any data in this block, add it to the buffer
                    if (result.Count > 0)
                        receiveStream.Write(buffer, 0, result.Count);
                }
            }
        } catch (WebSocketException) {
            return;
        } finally {
            // prevent any more messages from being sent
            _outputClosed.TrySetResult(false);
            // prevent any more messages from attempting to send
            operationMessageReceiveStream.Dispose();
        }
    }

    /// <inheritdoc/>
    public Task CloseConnectionAsync()
        => CloseConnectionAsync(1000, null);

    /// <inheritdoc/>
    public Task CloseConnectionAsync(int closeStatusId, string? closeDescription)
    {
        _pump.Post(new Message { CloseStatus = (WebSocketCloseStatus)closeStatusId, CloseDescription = closeDescription });
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task SendMessageAsync(OperationMessage message)
    {
        System.Diagnostics.Debug.WriteLine($"message sent: {message.Type}");
        _pump.Post(new Message { OperationMessage = message });
        return Task.CompletedTask;
    }

    private async Task SendMessageAsync(Message message)
    {
        if (_outputClosed.Task.IsCompleted)
            return;
        LastMessageSentAt = DateTime.UtcNow;
        if (message.OperationMessage != null) {
            await _serializer.WriteAsync(_stream, message.OperationMessage, _cancellationToken);
            await _stream.FlushAsync(_cancellationToken);
        } else {
            _outputClosed.TrySetResult(true);
            await _webSocket.CloseOutputAsync(message.CloseStatus, message.CloseDescription, _cancellationToken);
        }
    }

    private struct Message
    {
        public OperationMessage? OperationMessage;
        public WebSocketCloseStatus CloseStatus;
        public string? CloseDescription;
    }
}
