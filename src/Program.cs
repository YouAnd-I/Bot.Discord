using Ecs.Loop.Frent;
using Frent;
using Greet.Adapter.NetCord;
using Greet.System.Frent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Ping.Adapter.NetCord;
using Ping.System.Frent;
using Ticket.Adapter.Cloudflare;
using Ticket.Adapter.NetCord;
using Ticket.Data;
using Ticket.System.Frent;

using NetCord;

using NetCord.Hosting.Gateway;
using NetCord.Hosting.Services.ApplicationCommands;
using NetCord.Hosting.Services.ComponentInteractions;

using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;

var builder = Host.CreateApplicationBuilder(args);

// The game: one world and its rules, ticking on its own thread.
// Everything below only sees it as IWorldClient.
var tickets = new TicketSystem(TicketStore.Default);
var world = new FrentWorldLoop(new World(), PingSystem.Execute, GreetSystem.Execute, tickets.Execute);
world.AddNotificationDelivery<PriorityClassifyRequested>(); // TicketSystem → classifier adapter
builder.Services.AddHostedService(_ => new WorldTicker(world));

builder.Services
    .AddDiscordGateway(options => options.Intents = default)
    .AddApplicationCommands()
    .AddComponentInteractions<ButtonInteraction, ButtonInteractionContext>()
    .AddComponentInteractions<StringMenuInteraction, StringMenuInteractionContext>()
    .AddComponentInteractions<ModalInteraction, ModalInteractionContext>();

var host = builder.Build();

// 1. Slash command + ephemeral reply (only the caller sees it)
host.AddPing(world);

// 2. Typed options — delegate params become Discord options
host.AddGreet(world);

// 3. IT tickets: /it, its modal, and the card buttons — all through the world.
//    The Cloudflare (Workers AI clef) classifier is an adapter too: the world
//    asks it for priorities. Needs Cloudflare__AccountId/Cloudflare__ApiToken;
//    without them tickets still open, just urgent and marked offline.
host.AddItTickets(world);
world.AddCloudflareClassifier();

// 4. Subcommand group — /tools square, /tools echo
host.AddSlashCommandGroup("tools", "Utility commands", group =>
{
    group.AddSubCommand("square", "Square a number", (double a) => $"{a}² = {a * a}");
    group.AddSubCommand("echo", "Echo text back", (string text) => text);
});

// 5. Autocomplete option — suggestions while typing
host.AddSlashCommand("fruit", "Pick a fruit", (
    [SlashCommandParameter(Description = "Start typing to filter",
                           AutocompleteProviderType = typeof(FruitAutocompleteProvider))]
    string fruit) => $"You picked **{fruit}**!");

// 6. Components — buttons + select menu attached to the reply
host.AddSlashCommand("components", "Buttons and menus demo", () =>
    InteractionCallback.Message(new InteractionMessageProperties
    {
        Content = "Click a button or pick a color:",
        Components =
        [
            new ActionRowProperties
            {
                new ButtonProperties("btn-hello", "Say hi", ButtonStyle.Success),
                new LinkButtonProperties("https://netcord.dev", "NetCord docs"),
            },
            new StringMenuProperties("menu-color")
                {
                    new StringMenuSelectOptionProperties("Red", "red"),
                    new StringMenuSelectOptionProperties("Green", "green"),
                    new StringMenuSelectOptionProperties("Blue", "blue"),
                },
        ],
    }));

// 7. Modal — popup form
host.AddSlashCommand("form", "Open a modal form", () =>
    InteractionCallback.Modal(new ModalProperties("modal-hello", "Tell me something")
    {
        new LabelProperties("Message", new TextInputProperties("text", TextInputStyle.Paragraph)),
    }));

// 8. Context-menu commands — right-click a user or a message
host.AddUserCommand("User Info", (User user) =>
    InteractionCallback.Message(new InteractionMessageProperties
    {
        Content = $"{user} — ID `{user.Id}`",
        Flags = MessageFlags.Ephemeral,
    }));

host.AddMessageCommand("Echo Message", (RestMessage m) =>
    InteractionCallback.Message(new InteractionMessageProperties
    {
        Content = $"Echo: {m.Content}",
        Flags = MessageFlags.Ephemeral,
    }));

// Component interaction handlers — fire when the components are used
host.AddComponentInteraction<ButtonInteractionContext>("btn-hello", () => "Hi!");
host.AddComponentInteraction<StringMenuInteractionContext>("menu-color",
    (StringMenuInteractionContext c) => $"You chose: **{string.Join(", ", c.SelectedValues)}**");
host.AddComponentInteraction<ModalInteractionContext>("modal-hello",
    (ModalInteractionContext c) => "You wrote: " + string.Join(", ",
        c.Components.OfType<Label>().Select(l => l.Component).OfType<TextInput>().Select(i => i.Value)));

await host.RunAsync();

sealed class WorldTicker(FrentWorldLoop world) : BackgroundService
{
    // 20 ticks per second
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        world.RunAsync(TimeSpan.FromMilliseconds(50), stoppingToken);
}

public class FruitAutocompleteProvider : IAutocompleteProvider<AutocompleteInteractionContext>
{
    private static readonly string[] Fruits =
        ["apple", "apricot", "banana", "cherry", "dragonfruit", "elderberry",
         "fig", "grape", "kiwi", "lemon", "mango", "orange", "peach", "pear", "plum"];

    public ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>?> GetChoicesAsync(
        ApplicationCommandInteractionDataOption option, AutocompleteInteractionContext context)
    {
        var input = option.Value?.ToString() ?? string.Empty;
        var result = Fruits
            .Where(f => f.Contains(input, StringComparison.OrdinalIgnoreCase))
            .Take(25)
            .Select(f => new ApplicationCommandOptionChoiceProperties(f, f));
        return new(result);
    }
}
