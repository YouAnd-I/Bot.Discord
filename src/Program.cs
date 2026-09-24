using Microsoft.Extensions.Hosting;

using NetCord;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Hosting.Services.ApplicationCommands;
using NetCord.Hosting.Services.ComponentInteractions;
using System.Text.Json;

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
host.AddSlashCommand("it", "Create an IT ticket", async (
    ApplicationCommandContext c,
    [SlashCommandParameter(Description = "Short summary — suggests similar past tickets",
                           AutocompleteProviderType = typeof(TicketAutocompleteProvider))] string? title = null,
    [SlashCommandParameter(Description = "What happened?")] string? description = null,
    [SlashCommandParameter(Description = "How urgent is it?")] TicketPriority? priority = null,
    [SlashCommandParameter(Description = "Attach a screenshot or file")] Attachment? attachment = null) =>
{
    if (title is not null || description is not null || priority is not null || attachment is not null)
        return (InteractionCallbackProperties)await SendTicketAsync(c.User,
            c.Client.Rest, title, description, priority ?? TicketPriority.Urgent, attachment?.Url);

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
        new LabelProperties("Attachment", new FileUploadProperties("file") { Required = false, MaxValues = 1 }),
    });
});

host.AddComponentInteraction<ModalInteractionContext>("modal-it-ticket", async (ModalInteractionContext c) =>
{
    var fields = c.Components.OfType<Label>().Select(l => l.Component).ToList();
    var inputs = fields.OfType<TextInput>().ToList();
    var fileUrl = fields.OfType<FileUpload>().FirstOrDefault()?.Attachments.FirstOrDefault()?.Url;
    var priority = fields.OfType<StringMenu>().First().SelectedValues?.FirstOrDefault() switch
    {
        "no-rush" => TicketPriority.NoRush,
        "report" => TicketPriority.Report,
        _ => TicketPriority.Urgent,
    };
    return await SendTicketAsync(c.User, c.Client.Rest, inputs[0].Value, inputs[1].Value, priority, fileUrl);
});

// Status buttons — customId: itstatus:<Status>:<ticketId> (no dashes — they're param separators!)
host.AddComponentInteraction<ButtonInteractionContext>("itstatus",
    (ButtonInteractionContext c, string status, string ticketId) =>
{
    TicketStore.AppendStatus(c.User.ToString(), ticketId, status);
    var age = TicketStore.Age(ticketId);
    return InteractionCallback.ModifyMessage(m =>
    {
        m.Content = $"**IT ticket `{ticketId}`** — status updated to `{status}`" +
            (age is null ? "" : $" (open for {FormatAge(age.Value)})");
        m.Components = [];
    });
});

// Report button — opens a confidential complaint form: itreport:<ticketId>
host.AddComponentInteraction<ButtonInteractionContext>("itreport",
    (ButtonInteractionContext c, string ticketId) =>
    InteractionCallback.Modal(new ModalProperties($"reportmodal:{ticketId}", "Confidential Report")
    {
        new TextDisplayProperties("**This report is confidential** — it won't be shown publicly."),
        new LabelProperties("Your complaint", new TextInputProperties("complaint", TextInputStyle.Paragraph)),
        new LabelProperties("What action should have been taken?", new TextInputProperties("action", TextInputStyle.Paragraph)),
        new LabelProperties("Evidence", new FileUploadProperties("reportfile") { Required = false, MaxValues = 1 }),
        new LabelProperties("Stay anonymous", new CheckboxProperties("anonymous") { Default = true })
        { Description = "We won't attach your name to this report" },
    }));

host.AddComponentInteraction<ModalInteractionContext>("reportmodal", (ModalInteractionContext c, string ticketId) =>
{
    var fields = c.Components.OfType<Label>().Select(l => l.Component).ToList();
    var inputs = fields.OfType<TextInput>().ToList();
    var fileUrl = fields.OfType<FileUpload>().FirstOrDefault()?.Attachments.FirstOrDefault()?.Url;
    var anonymous = fields.OfType<Checkbox>().FirstOrDefault()?.Checked ?? true;
    TicketStore.AppendReport(c.User.ToString(), ticketId, inputs[0].Value, inputs[1].Value, anonymous, fileUrl);
    var age = TicketStore.Age(ticketId);
    return InteractionCallback.ModifyMessage(m =>
    {
        m.Content = $"**IT ticket `{ticketId}`** — status updated to `complete`" +
            (age is null ? "" : $" (open for {FormatAge(age.Value)})");
        m.Components = [];
    });
});

static string FormatAge(TimeSpan a) => a.TotalHours >= 1
    ? $"{(int)a.TotalHours}h {a.Minutes}m"
    : a.TotalMinutes >= 1 ? $"{(int)a.TotalMinutes}m" : $"{(int)a.TotalSeconds}s";

// Builds the ticket, DMs the full card to the user, replies with a short ephemeral ack
static async Task<InteractionCallbackProperties> SendTicketAsync(
    User user, RestClient rest, string? title, string? description,
    TicketPriority priority, string? attachmentUrl)
{
    var (ticketId, content, buttons) = BuildTicket(
        user.ToString(), title, description, priority, attachmentUrl);
    try
    {
        var dm = await user.GetDMChannelAsync();
        await rest.SendMessageAsync(dm.Id,
            new MessageProperties { Content = content, Components = [buttons] });
        return InteractionCallback.Message(new InteractionMessageProperties
        {
            Content = $"**IT ticket `{ticketId}` created** — sent to your DMs 📬",
            Flags = MessageFlags.Ephemeral,
        });
    }
    catch
    {
        // DMs closed — fall back to an ephemeral reply with the full card
        return InteractionCallback.Message(new InteractionMessageProperties
        {
            Content = content, Flags = MessageFlags.Ephemeral, Components = [buttons],
        });
    }
}

static (string Id, string Content, ActionRowProperties Buttons) BuildTicket(
    string user, string? title, string? description, TicketPriority priority, string? attachmentUrl)
{
    var ticketId = Guid.NewGuid().ToString("N")[..8];
    TicketStore.Append(user, ticketId, PriorityName(priority), title, description, attachmentUrl);

    var created = DateTimeOffset.UtcNow;
    var content = $"**IT ticket `{ticketId}` created** — <t:{created.ToUnixTimeSeconds()}:R>\n" +
        $"Title: **{title}**\nPriority: `{PriorityName(priority)}`\n> {description}";
    if (attachmentUrl is not null)
        content += $"\n📎 {attachmentUrl}";

    var solution = TicketStore.BestSolution($"{title} {description}");
    if (solution is not null)
    {
        content += $"\n\n**💡 IT solution — {solution.Title}:**\n{solution.Text}";
        if (solution.Image is not null)
            content += $"\n{solution.Image}";
    }

    var buttons = new ActionRowProperties
    {
        new ButtonProperties($"itstatus:complete:{ticketId}", "Complete", ButtonStyle.Success),
        new ButtonProperties($"itstatus:unsolved:{ticketId}", "UnSolved", ButtonStyle.Danger),
        new ButtonProperties($"itstatus:planned:{ticketId}", "Planned for future", ButtonStyle.Primary),
        new ButtonProperties($"itreport:{ticketId}", "Report", ButtonStyle.Secondary),
    };
    return (ticketId, content + "\n\n*Set status:*", buttons);
}

static string PriorityName(TicketPriority p) => p switch
{
    TicketPriority.NoRush => "no-rush",
    TicketPriority.Report => "report",
    _ => "urgent",
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

// Ticket storage — append-only txt, parseable "key=value" fields so we can search back
public static class TicketStore
{
    public const string FileName = "it-tickets.txt";
    public const string Dir = "it-tickets";

    public record Ticket(string Id, string Title, string Desc);
    public record Solution(string Title, string? Text, string? Image);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static void Append(string user, string id, string priority,
        string? title, string? desc, string? file)
    {
        var line = $"[{DateTimeOffset.UtcNow:u}] user={user} ticket={id} priority={priority} | title={Clean(title) ?? "(no title)"} | desc={Clean(desc)}";
        if (file is not null) line += $" | file={file}";
        File.AppendAllText(FileName, line + '\n');

        Directory.CreateDirectory(Dir);
        File.WriteAllText($"{Dir}/{Slug(title)}-{id}.json",
            JsonSerializer.Serialize(new
            {
                id, user,
                created = DateTimeOffset.UtcNow.ToString("u"),
                priority,
                title,
                description = desc,
                file,
                status = "open",
            }, JsonOpts));
    }

    // Solutions written by IT as {slug}.s.json — { "title": "...", "text": "...", "image": "url" }
    public static Solution? BestSolution(string? query)
    {
        var words = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => w.Length > 1).ToArray();
        if (words.Length == 0 || !Directory.Exists(Dir)) return null;

        return Directory.EnumerateFiles(Dir, "*.s.json")
            .Select(f =>
            {
                try { return JsonSerializer.Deserialize<Solution>(File.ReadAllText(f), JsonOpts); }
                catch { return null; }
            })
            .Where(s => s is not null)
            .Select(s => (s: s!, score: words.Count(w =>
                s!.Title.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                (s.Text ?? "").Contains(w, StringComparison.OrdinalIgnoreCase))))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .Select(x => x.s)
            .FirstOrDefault();
    }

    private static string Slug(string? title)
    {
        var s = string.Concat((title ?? "untitled").ToLowerInvariant().Trim()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-'));
        while (s.Contains("--")) s = s.Replace("--", "-");
        return s.Trim('-') is { Length: > 0 } x ? x : "untitled";
    }

    public static void AppendStatus(string user, string id, string status) =>
        File.AppendAllText(FileName, $"[{DateTimeOffset.UtcNow:u}] user={user} ticket={id} status={status}\n");

    public static void AppendReport(string user, string id, string complaint, string action,
        bool anonymous, string? file) =>
        File.AppendAllText(FileName,
            $"[{DateTimeOffset.UtcNow:u}] user={(anonymous ? "anonymous" : user)} ticket={id} report | complaint={Clean(complaint)} | action={Clean(action)}" +
            (file is null ? "" : $" | file={file}") + "\n");

    // Time since the ticket's creation line
    public static TimeSpan? Age(string id)
    {
        if (!File.Exists(FileName)) return null;
        foreach (var line in File.ReadLines(FileName))
        {
            if (!line.Contains($"ticket={id}") || !line.Contains(" | title=")) continue;
            var close = line.IndexOf(']');
            if (line.StartsWith('[') && close > 1 &&
                DateTimeOffset.TryParse(line[1..close], out var created))
                return DateTimeOffset.UtcNow - created;
        }
        return null;
    }

    public static List<Ticket> Load()
    {
        var list = new List<Ticket>();
        if (!File.Exists(FileName)) return list;
        foreach (var line in File.ReadLines(FileName))
        {
            var parts = line.Split(" | ");
            var id = parts[0].Split(' ').FirstOrDefault(p => p.StartsWith("ticket="))?[7..];
            var title = parts.ElementAtOrDefault(1);
            if (id is null || title is null || !title.StartsWith("title=")) continue;
            var desc = parts.ElementAtOrDefault(2);
            list.Add(new(id, title[6..], desc is not null && desc.StartsWith("desc=") ? desc[5..] : ""));
        }
        return list;
    }

    // Silly search: score = words found in title or description
    public static IEnumerable<Ticket> Similar(string? query, int take = 25, string? excludeId = null)
    {
        var words = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => w.Length > 1).ToArray();
        if (words.Length == 0) return [];
        return Load()
            .Where(t => t.Id != excludeId)
            .Select(t => (t, score: words.Count(w =>
                t.Title.Contains(w, StringComparison.OrdinalIgnoreCase) ||
                t.Desc.Contains(w, StringComparison.OrdinalIgnoreCase))))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .Take(take)
            .Select(x => x.t);
    }

    private static string? Clean(string? s) =>
        s?.Replace("\r", " ").Replace("\n", " ").Replace("|", "/");
}

public enum TicketPriority
{
    [SlashCommandChoice(Name = "urgent")] Urgent,
    [SlashCommandChoice(Name = "no-rush")] NoRush,
    [SlashCommandChoice(Name = "report")] Report,
}

public class TicketAutocompleteProvider : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>?> GetChoicesAsync(
        ApplicationCommandInteractionDataOption option, AutocompleteInteractionContext context)
    {
        var input = option.Value?.ToString() ?? "";
        var titles = string.IsNullOrWhiteSpace(input)
            ? TicketStore.Load().Select(t => t.Title).TakeLast(25)
            : TicketStore.Similar(input).Select(t => t.Title);
        return new(titles.Select(t => new ApplicationCommandOptionChoiceProperties(
            t.Length > 100 ? t[..100] : t, t.Length > 100 ? t[..100] : t)));
    }
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
