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
        options.Intents = default;
    })
    .AddApplicationCommands();

var host = builder.Build();

host.AddSlashCommand("ping", "Ping pong!", () =>
    InteractionCallback.Message(new InteractionMessageProperties
    {
        Content = "pong",
        Flags = MessageFlags.Ephemeral,
    }));

await host.RunAsync();
