using GraphQL;
using GraphQL.AspNetCore3;
using GraphQL.MicrosoftDI;
using GraphQL.SystemTextJson;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<Chat.Services.ChatService>();
builder.Services.AddGraphQL(b => b
    .AddAutoSchema<Chat.Schema.Query>(s => s
        .WithMutation<Chat.Schema.Mutation>()
        .WithSubscription<Chat.Schema.Subscription>())
    .AddSystemTextJson());
builder.Services.AddRouting();

var app = builder.Build();
app.UseDeveloperExceptionPage();
app.UseWebSockets();
app.UseRouting();
app.UseEndpoints(endpoints => {
    // configure the graphql endpoint at "/graphql"
    endpoints.MapGraphQL("/graphql");
    // configure Playground at "/"
    endpoints.MapGraphQLPlayground("/");
});
await app.RunAsync();
