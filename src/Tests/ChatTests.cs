using System.Security.Claims;

namespace Tests;

public class ChatTests : IDisposable
{
    private readonly TestServer _app;
    private GraphQLHttpMiddlewareOptions _options = null!;

    public ChatTests()
    {
        _app = new(ConfigureBuilder());
    }

    private IWebHostBuilder ConfigureBuilder()
    {
        var hostBuilder = new WebHostBuilder();
        hostBuilder.ConfigureServices(services => {
            services.AddSingleton<Chat.Services.ChatService>();
            services.AddGraphQL(b => b
                .AddAutoSchema<Chat.Schema.Query>(s => s
                    .WithMutation<Chat.Schema.Mutation>()
                    .WithSubscription<Chat.Schema.Subscription>())
                .AddSystemTextJson());
        });
        hostBuilder.Configure(app => {
            app.UseWebSockets();
            app.UseGraphQL("/graphql", o => {
                _options = o;
            });
        });
        return hostBuilder;
    }

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task Count()
    {
        var str = await _app.ExecuteGet("/graphql?query={count}");
        str.ShouldBe("{\"data\":{\"count\":0}}");
    }

    [Fact]
    public async Task Multiple()
    {
        var client = _app.CreateClient();
        var content = new StringContent("[{\"query\":\"{count}\"},{\"query\":\"{count}\"}]", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/graphql", content);
        response.EnsureSuccessStatusCode();
        var str = await response.Content.ReadAsStringAsync();
        str.ShouldBe("[{\"data\":{\"count\":0}},{\"data\":{\"count\":0}}]");
    }

    [Fact]
    public async Task AddMessage()
    {
        await AddMessageInternal(1);

        var str = await _app.ExecuteGet("/graphql?query={count}");
        str.ShouldBe("{\"data\":{\"count\":1}}");
    }

    [Fact]
    public async Task LastMessage()
    {
        await AddMessageInternal(1);

        var str = await _app.ExecuteGet("/graphql?query={lastMessage{id message}}");
        str.ShouldBe("{\"data\":{\"lastMessage\":{\"id\":\"1\",\"message\":\"hello\"}}}");
    }

    [Fact]
    public async Task AllMessages()
    {
        await AddMessageInternal(1);
        await AddMessageInternal(2);

        var str = await _app.ExecuteGet("/graphql?query={allMessages{id message}}");
        str.ShouldBe("{\"data\":{\"allMessages\":[{\"id\":\"1\",\"message\":\"hello\"},{\"id\":\"2\",\"message\":\"hello\"}]}}");
    }

    private async Task AddMessageInternal(int id, string name = "John Doe")
    {
        var str = await _app.ExecutePost(
            "/graphql",
            "mutation {addMessage(message:{message:\"hello\",from:\"" + name + "\"}){id}}");
        str.ShouldBe("{\"data\":{\"addMessage\":{\"id\":\"" + id + "\"}}}");
    }

    [Fact]
    public async Task DeleteMessage()
    {
        await AddMessageInternal(1);
        await AddMessageInternal(2);

        var str = await _app.ExecuteGet("/graphql?query={count}");
        str.ShouldBe("{\"data\":{\"count\":2}}");

        await DeleteMessageInternal(1);

        str = await _app.ExecuteGet("/graphql?query={count}");
        str.ShouldBe("{\"data\":{\"count\":1}}");
    }

    private async Task DeleteMessageInternal(int id)
    {
        var str = await _app.ExecutePost(
            "/graphql",
            "mutation {deleteMessage(id:\"" + id + "\"){id}}");
        str.ShouldBe("{\"data\":{\"deleteMessage\":{\"id\":\"" + id + "\"}}}");
    }

    [Fact]
    public async Task ClearMessages()
    {
        await AddMessage();
        await ClearMessagesInternal(1);
    }

    private async Task ClearMessagesInternal(int numDeleted)
    {
        var str = await _app.ExecutePost(
            "/graphql",
            "mutation {clearMessages}");
        str.ShouldBe("{\"data\":{\"clearMessages\":" + numDeleted + "}}");
    }

    [Theory]
    [InlineData("graphql-ws")]
    [InlineData("graphql-transport-ws")]
    public async Task Subscription(string subProtocol)
    {
        // create websocket connection
        var webSocketClient = _app.CreateWebSocketClient();
        webSocketClient.ConfigureRequest = request => {
            request.Headers["Sec-WebSocket-Protocol"] = subProtocol;
        };
        webSocketClient.SubProtocols.Add(subProtocol);
        using var webSocket = await webSocketClient.ConnectAsync(new Uri(_app.BaseAddress, "/graphql"), default);

        // send CONNECTION_INIT
        await webSocket.SendMessageAsync(new OperationMessage {
            Type = "connection_init"
        });

        // wait for CONNECTION_ACK
        var message = await webSocket.ReceiveMessageAsync();
        message.Type.ShouldBe("connection_ack");

        // subscribe
        await webSocket.SendMessageAsync(new OperationMessage {
            Type = subProtocol == "graphql-ws" ? "start" : "subscribe",
            Id = "123",
            Payload = new GraphQLRequest {
                Query = "subscription { events { type message { id message from } } }",
            },
        });

        await Task.Delay(1000);

        // post a new message on a separate thread
        _ = Task.Run(() => AddMessageInternal(1));

        // wait for a new message sent over this websocket
        message = await webSocket.ReceiveMessageAsync();
        message.Type.ShouldBe(subProtocol == "graphql-ws" ? "data" : "next");
        message.Payload.ShouldBe(@"{""data"":{""events"":{""type"":""NEW_MESSAGE"",""message"":{""id"":""1"",""message"":""hello"",""from"":""John Doe""}}}}");

        // clear messages
        _ = Task.Run(async () => {
            await ClearMessagesInternal(1);
        });

        // wait for a clear message sent over this websocket
        message = await webSocket.ReceiveMessageAsync();
        message.Type.ShouldBe(subProtocol == "graphql-ws" ? "data" : "next");
        message.Payload.ShouldBe(@"{""data"":{""events"":{""type"":""CLEAR_MESSAGES"",""message"":null}}}");

        // post a new message on a separate thread
        _ = Task.Run(() => AddMessageInternal(2));

        // wait for a new message sent over this websocket
        message = await webSocket.ReceiveMessageAsync();
        message.Type.ShouldBe(subProtocol == "graphql-ws" ? "data" : "next");
        message.Payload.ShouldBe(@"{""data"":{""events"":{""type"":""NEW_MESSAGE"",""message"":{""id"":""2"",""message"":""hello"",""from"":""John Doe""}}}}");

        // delete a message on a separate thread
        _ = Task.Run(() => DeleteMessageInternal(2));

        // wait for a delete message sent over this websocket
        message = await webSocket.ReceiveMessageAsync();
        message.Type.ShouldBe(subProtocol == "graphql-ws" ? "data" : "next");
        message.Payload.ShouldBe(@"{""data"":{""events"":{""type"":""DELETE_MESSAGE"",""message"":{""id"":""2"",""message"":""hello"",""from"":""John Doe""}}}}");

        // unsubscribe
        await webSocket.SendMessageAsync(new OperationMessage {
            Type = subProtocol == "graphql-ws" ? "stop" : "complete",
            Id = "123",
        });

        // initiate closure
        if (subProtocol == "graphql-ws") {
            // send close message
            await webSocket.SendMessageAsync(new OperationMessage {
                Type = "connection_terminate",
            });
        } else {
            // close websocket
            await webSocket.CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, null, default);
        }

        // wait for websocket closure
        (await webSocket.ReceiveCloseAsync()).ShouldBe(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure);
    }

    [Theory]
    [InlineData("graphql-ws", null)]
    [InlineData("graphql-transport-ws", null)]
    [InlineData("graphql-ws", "John Doe")]
    [InlineData("graphql-transport-ws", "John Doe")]
    [InlineData("graphql-ws", "test")]
    [InlineData("graphql-transport-ws", "test")]
    public async Task Subscription_NewMessages(string subProtocol, string? from)
    {
        // create websocket connection
        var webSocketClient = _app.CreateWebSocketClient();
        webSocketClient.ConfigureRequest = request => {
            request.Headers["Sec-WebSocket-Protocol"] = subProtocol;
        };
        webSocketClient.SubProtocols.Add(subProtocol);
        using var webSocket = await webSocketClient.ConnectAsync(new Uri(_app.BaseAddress, "/graphql"), default);

        // send CONNECTION_INIT
        await webSocket.SendMessageAsync(new OperationMessage {
            Type = "connection_init"
        });

        // wait for CONNECTION_ACK
        var message = await webSocket.ReceiveMessageAsync();
        message.Type.ShouldBe("connection_ack");

        // subscribe
        await webSocket.SendMessageAsync(new OperationMessage {
            Type = subProtocol == "graphql-ws" ? "start" : "subscribe",
            Id = "123",
            Payload = new GraphQLRequest {
                Query = "subscription { newMessages(from:" + (from == null ? "null" : $"\"{from}\"") + ") { id message from } }",
            },
        });

        await Task.Delay(1000);

        // post a new message
        await AddMessageInternal(1);
        await AddMessageInternal(2, "test");

        if (from == "John Doe" || from == null) {
            // wait for a new message sent over this websocket
            message = await webSocket.ReceiveMessageAsync();
            message.Type.ShouldBe(subProtocol == "graphql-ws" ? "data" : "next");
            message.Payload.ShouldBe(@"{""data"":{""newMessages"":{""id"":""1"",""message"":""hello"",""from"":""John Doe""}}}");
        }
        if (from == "test" || from == null) {
            // wait for a new message sent over this websocket
            message = await webSocket.ReceiveMessageAsync();
            message.Type.ShouldBe(subProtocol == "graphql-ws" ? "data" : "next");
            message.Payload.ShouldBe(@"{""data"":{""newMessages"":{""id"":""2"",""message"":""hello"",""from"":""test""}}}");
        }

        // send a ping
        if (subProtocol == "graphql-transport-ws") {
            await webSocket.SendMessageAsync(new OperationMessage {
                Type = "ping",
            });
            message = await webSocket.ReceiveMessageAsync();
            message.Type.ShouldBe("pong");

            // and an unsolicited pong, with no expected response
            await webSocket.SendMessageAsync(new OperationMessage {
                Type = "pong",
            });
        }

        // unsubscribe
        await webSocket.SendMessageAsync(new OperationMessage {
            Type = subProtocol == "graphql-ws" ? "stop" : "complete",
            Id = "123",
        });

        // initiate closure
        if (subProtocol == "graphql-ws") {
            // send close message
            await webSocket.SendMessageAsync(new OperationMessage {
                Type = "connection_terminate",
            });
        } else {
            // close websocket
            await webSocket.CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, null, default);
        }

        // wait for websocket closure
        (await webSocket.ReceiveCloseAsync()).ShouldBe(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure);
    }

    [Theory]
    [InlineData("graphql-ws")]
    [InlineData("graphql-transport-ws")]
    public async Task Subscription_AuthorizationFailed(string subProtocol)
    {
        var builder = ConfigureBuilder();
        var mockAuthorizationService = new Mock<IWebSocketAuthenticationService>(MockBehavior.Strict);
        mockAuthorizationService.Setup(x => x.AuthenticateAsync(It.IsAny<AuthenticationRequest>())).Returns<AuthenticationRequest>(request => {
            request.SubProtocol.ShouldBe(subProtocol);
            return Task.CompletedTask;
        }).Verifiable();
        builder.ConfigureServices(s => s.AddSingleton(mockAuthorizationService.Object));
        using var app = new TestServer(builder);
        _options.AuthorizationRequired = true;

        // create websocket connection
        var webSocketClient = app.CreateWebSocketClient();
        webSocketClient.ConfigureRequest = request => {
            request.Headers["Sec-WebSocket-Protocol"] = subProtocol;
        };
        webSocketClient.SubProtocols.Add(subProtocol);
        using var webSocket = await webSocketClient.ConnectAsync(new Uri(_app.BaseAddress, "/graphql"), default);

        // send CONNECTION_INIT
        await webSocket.SendMessageAsync(new OperationMessage {
            Type = "connection_init"
        });

        if (subProtocol == "graphql-ws") {
            // wait for CONNECTION_ERROR
            var message = await webSocket.ReceiveMessageAsync();
            message.Type.ShouldBe("connection_error");
            message.Payload.ShouldBeOfType<string>().ShouldBe("\"Access denied\""); // for the purposes of testing, this contains the raw JSON received for this JSON element.
        }

        // wait for websocket closure
        (await webSocket.ReceiveCloseAsync()).ShouldBe((System.Net.WebSockets.WebSocketCloseStatus)4401);

        mockAuthorizationService.Verify();
    }

    [Theory]
    [InlineData("graphql-ws", false)]
    [InlineData("graphql-ws", true)]
    [InlineData("graphql-transport-ws", false)]
    [InlineData("graphql-transport-ws", true)]
    public async Task Subscription_Authentication(string subProtocol, bool successfulAuthentication)
    {
        var builder = ConfigureBuilder();
        var mockAuthorizationService = new Mock<IWebSocketAuthenticationService>(MockBehavior.Strict);
        mockAuthorizationService.Setup(x => x.AuthenticateAsync(It.IsAny<AuthenticationRequest>()))
            .Returns<AuthenticationRequest>((authenticationRequest) => {
                var connection = authenticationRequest.Connection;
                var message = authenticationRequest.OperationMessage;
                authenticationRequest.SubProtocol.ShouldBe(subProtocol);
                connection.HttpContext.User.Identity!.IsAuthenticated.ShouldBeFalse();
                var serializer = connection.HttpContext.RequestServices.GetRequiredService<IGraphQLSerializer>();
                var payload = serializer.ReadNode<Inputs>(message.Payload);
                var valueString = payload?.TryGetValue("Authorization", out var value) == true && value is string str ? str : null;
                valueString.ShouldBe("Bearer testing");
                var claimsPrincipal = new ClaimsPrincipal(new ClaimsIdentity("Bearer"));
                if (successfulAuthentication)
                    connection.HttpContext.User = claimsPrincipal;
                return Task.CompletedTask;
            }).Verifiable();
        builder.ConfigureServices(s => s.AddSingleton(mockAuthorizationService.Object));
        using var app = new TestServer(builder);
        _options.AuthorizationRequired = true;

        // create websocket connection
        var webSocketClient = app.CreateWebSocketClient();
        webSocketClient.ConfigureRequest = request => {
            request.Headers["Sec-WebSocket-Protocol"] = subProtocol;
        };
        webSocketClient.SubProtocols.Add(subProtocol);
        using var webSocket = await webSocketClient.ConnectAsync(new Uri(_app.BaseAddress, "/graphql"), default);

        // send CONNECTION_INIT
        await webSocket.SendMessageAsync(new OperationMessage {
            Type = "connection_init",
            Payload = new {
                Authorization = "Bearer testing",
            },
        });

        if (successfulAuthentication) {
            // wait for CONNECTION_ACK
            var message = await webSocket.ReceiveMessageAsync();
            message.Type.ShouldBe("connection_ack");
        } else {
            if (subProtocol == "graphql-ws") {
                // wait for CONNECTION_ERROR
                var message = await webSocket.ReceiveMessageAsync();
                message.Type.ShouldBe("connection_error");
                message.Payload.ShouldBeOfType<string>().ShouldBe("\"Access denied\""); // for the purposes of testing, this contains the raw JSON received for this JSON element.
            }

            // wait for websocket closure
            (await webSocket.ReceiveCloseAsync()).ShouldBe((System.Net.WebSockets.WebSocketCloseStatus)4401);
        }

        mockAuthorizationService.Verify();
    }

}
