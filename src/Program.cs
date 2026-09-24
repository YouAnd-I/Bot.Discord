using Microsoft.Extensions.Hosting;

using NetCord;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Hosting.Services.ApplicationCommands;
using NetCord.Rest;

var builder = Host.CreateApplicationBuilder(args);

builder.Services
    .AddDiscordGateway(options =>
    {
        options.Intents = GatewayIntents.GuildMessages
                          | GatewayIntents.DirectMessages
                          | GatewayIntents.MessageContent;
    })
    .AddGatewayHandlers(typeof(Program).Assembly)
    .AddApplicationCommands();

var host = builder.Build();

host.AddSlashCommand("ping", "Ping pong!", () =>
    InteractionCallback.Message(new InteractionMessageProperties
    {
        Content = "pong",
        Flags = MessageFlags.Ephemeral,
    }));

await host.RunAsync();

public class PingPongHandler(GatewayClient client) : IMessageCreateGatewayHandler
{
    public async ValueTask HandleAsync(Message message)
    {
        if (message.Author?.IsBot == true)
            return;

        if (message.Content.Trim().Equals("ping", StringComparison.OrdinalIgnoreCase))
            await client.Rest.SendMessageAsync(message.ChannelId, "pong");
    }
}
