using NetCord;
using NetCord.Gateway;
using NetCord.Logging;

var token = Environment.GetEnvironmentVariable("Discord__Token")
    ?? throw new InvalidOperationException("Discord__Token environment variable is not set");

GatewayClient client = new(new BotToken(token), new GatewayClientConfiguration
{
    Intents = GatewayIntents.GuildMessages
              | GatewayIntents.DirectMessages
              | GatewayIntents.MessageContent,
    Logger = new ConsoleLogger(),
});

client.MessageCreate += async message =>
{
    if (message.Author?.IsBot == true)
        return;

    if (message.Content.Trim().Equals("ping", StringComparison.OrdinalIgnoreCase))
        await client.Rest.SendMessageAsync(message.ChannelId, "pong");
};

await client.StartAsync();
await Task.Delay(-1);
