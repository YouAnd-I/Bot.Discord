using Microsoft.Extensions.Hosting;

using NetCord;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Hosting.Services.ApplicationCommands;
using NetCord.Hosting.Services.ComponentInteractions;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;

var builder = Host.CreateApplicationBuilder(args);

builder.Services
    .AddDiscordGateway(options => options.Intents = default)
    .AddApplicationCommands()
    .AddComponentInteractions<ButtonInteraction, ButtonInteractionContext>()
    .AddComponentInteractions<StringMenuInteraction, StringMenuInteractionContext>()
    .AddComponentInteractions<ModalInteraction, ModalInteractionContext>();

var host = builder.Build();

// 1. Slash command + ephemeral reply (only the caller sees it)
host.AddSlashCommand("ping", "Ping pong!", () =>
    InteractionCallback.Message(new InteractionMessageProperties
    {
        Content = "pong",
        Flags = MessageFlags.Ephemeral,
    }));

// 2. Typed options — delegate params become Discord options
host.AddSlashCommand("greet", "Greet someone!", (User user, string message) =>
    $"{message}, {user}!");

// 3. Subcommand group — /tools square, /tools echo
host.AddSlashCommandGroup("tools", "Utility commands", group =>
{
    group.AddSubCommand("square", "Square a number", (double a) => $"{a}² = {a * a}");
    group.AddSubCommand("echo", "Echo text back", (string text) => text);
});

// 4. Autocomplete option — suggestions while typing
host.AddSlashCommand("fruit", "Pick a fruit", (
    [SlashCommandParameter(Description = "Start typing to filter",
                           AutocompleteProviderType = typeof(FruitAutocompleteProvider))]
    string fruit) => $"You picked **{fruit}**!");

// 5. Components — buttons + select menu attached to the reply
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

// 6. Modal — popup form
host.AddSlashCommand("form", "Open a modal form", () =>
    InteractionCallback.Modal(new ModalProperties("modal-hello", "Tell me something")
    {
        new LabelProperties("Message", new TextInputProperties("text", TextInputStyle.Paragraph)),
    }));

// 7. /it — IT ticket. Bare /it → modal form. Any option → ticket created directly.
host.AddSlashCommand("it", "Create an IT ticket", (
    ApplicationCommandContext c,
    [SlashCommandParameter(Description = "Short summary")] string? title = null,
    [SlashCommandParameter(Description = "What happened?")] string? description = null,
    [SlashCommandParameter(Description = "How urgent is it?")] TicketPriority? priority = null) =>
{
    if (title is not null || description is not null || priority is not null)
        return (InteractionCallbackProperties)InteractionCallback.Message(SaveTicket(
            c.User.ToString(), title, description, priority ?? TicketPriority.Urgent));

    return InteractionCallback.Modal(new ModalProperties("modal-it-ticket", "New IT Ticket")
    {
        new LabelProperties("Title", new TextInputProperties("title", TextInputStyle.Short)),
        new LabelProperties("Description", new TextInputProperties("description", TextInputStyle.Paragraph)),
        new LabelProperties("Priority", new StringMenuProperties("priority")
        {
            new StringMenuSelectOptionProperties("Urgent", "urgent") { Default = true },
            new StringMenuSelectOptionProperties("No rush", "no-rush"),
            new StringMenuSelectOptionProperties("Report", "report"),
        }),
    });
});

host.AddComponentInteraction<ModalInteractionContext>("modal-it-ticket", (ModalInteractionContext c) =>
{
    var fields = c.Components.OfType<Label>().Select(l => l.Component).ToList();
    var inputs = fields.OfType<TextInput>().ToList();
    var priority = fields.OfType<StringMenu>().First().SelectedValues?.FirstOrDefault() switch
    {
        "no-rush" => TicketPriority.NoRush,
        "report" => TicketPriority.Report,
        _ => TicketPriority.Urgent,
    };
    return InteractionCallback.Message(
        SaveTicket(c.User.ToString(), inputs[0].Value, inputs[1].Value, priority));
});

// Status buttons — each carries the ticket id in its customId: it-status:<Status>:<ticketId>
host.AddComponentInteraction<ButtonInteractionContext>("it-status",
    (ButtonInteractionContext c, TicketStatus status, string ticketId) =>
{
    File.AppendAllText("it-tickets.txt",
        $"[{DateTimeOffset.UtcNow:u}] user={c.User} ticket={ticketId} status={StatusName(status)}\n");
    return InteractionCallback.ModifyMessage(m =>
    {
        m.Content = $"**IT ticket `{ticketId}`** — status updated to `{StatusName(status)}`";
        m.Components = [];
    });
});

static InteractionMessageProperties SaveTicket(
    string user, string? title, string? description, TicketPriority priority)
{
    var ticketId = Guid.NewGuid().ToString("N")[..8];
    File.AppendAllText("it-tickets.txt",
        $"[{DateTimeOffset.UtcNow:u}] user={user} ticket={ticketId} priority={PriorityName(priority)} | {title ?? "(no title)"} — {description}\n");
    return new InteractionMessageProperties
    {
        Content = $"**IT ticket `{ticketId}` created**\nTitle: **{title}**\nPriority: `{PriorityName(priority)}`\n> {description}\n\n*Set status:*",
        Flags = MessageFlags.Ephemeral,
        Components =
        [
            new ActionRowProperties
            {
                new ButtonProperties($"it-status:Solved:{ticketId}", "Solved", ButtonStyle.Success),
                new ButtonProperties($"it-status:NoVisit:{ticketId}", "No visit needed", ButtonStyle.Secondary),
                new ButtonProperties($"it-status:Unresolved:{ticketId}", "Unresolved", ButtonStyle.Danger),
                new ButtonProperties($"it-status:Planned:{ticketId}", "Planned for future", ButtonStyle.Primary),
            },
        ],
    };
}

static string PriorityName(TicketPriority p) => p switch
{
    TicketPriority.NoRush => "no-rush",
    TicketPriority.Report => "report",
    _ => "urgent",
};

static string StatusName(TicketStatus s) => s switch
{
    TicketStatus.NoVisit => "no-visit-needed",
    TicketStatus.Unresolved => "unresolved",
    TicketStatus.Planned => "planned-for-future",
    _ => "solved",
};

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

public enum TicketStatus { Solved, NoVisit, Unresolved, Planned }

public enum TicketPriority
{
    [SlashCommandChoice(Name = "urgent")] Urgent,
    [SlashCommandChoice(Name = "no-rush")] NoRush,
    [SlashCommandChoice(Name = "report")] Report,
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
