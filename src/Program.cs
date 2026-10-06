using Ecs.Loop.Frent;
using Frent;
using Greet.Adapter.NetCord;
using Greet.System.Frent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Ping.Adapter.NetCord;
using Ping.System.Frent;

using NetCord;

using NetCord.Hosting.Gateway;
using NetCord.Hosting.Services.ApplicationCommands;
using NetCord.Hosting.Services.ComponentInteractions;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using NetCord.Rest;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.ComponentInteractions;

var builder = Host.CreateApplicationBuilder(args);

// The game: one world and its rules, ticking on its own thread.
// Everything below only sees it as IWorldClient.
var world = new FrentWorldLoop(new World(), PingSystem.Execute, GreetSystem.Execute);
builder.Services.AddHostedService(_ => new WorldTicker(world));

builder.Services
    .AddDiscordGateway(options => options.Intents = default)
    .AddApplicationCommands()
    .AddComponentInteractions<ButtonInteraction, ButtonInteractionContext>()
    .AddComponentInteractions<StringMenuInteraction, StringMenuInteractionContext>()
    .AddComponentInteractions<ModalInteraction, ModalInteractionContext>();

var host = builder.Build();

var laya = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

// Default ticket assignee — Discord user snowflake via Discord__ItUser env
var itUserId = ulong.TryParse(
    Environment.GetEnvironmentVariable("Discord__ItUser"), out var u) ? u : (ulong?)null;

// 1. Slash command + ephemeral reply (only the caller sees it)
host.AddPing(world);

// 2. Typed options — delegate params become Discord options
host.AddGreet(world);

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

// 7. /it — instant ticket with defaults. form:True → full modal instead.
host.AddSlashCommand("it", "Create an IT ticket", (
    ApplicationCommandContext c,
    [SlashCommandParameter(Description = "Short summary")] string? title = null,
    [SlashCommandParameter(Description = "What happened?")] string? description = null,
    [SlashCommandParameter(Description = "How urgent is it?")] TicketPriority? priority = null,
    [SlashCommandParameter(Description = "Attach a screenshot or file")] Attachment? attachment = null,
    [SlashCommandParameter(Description = "Who should handle this (defaults to on-call IT)")] User? assignee = null) =>
{
    // Bare /it → form. Any option → instant ticket.
    if (title is not null || description is not null || priority is not null || attachment is not null || assignee is not null)
    {
        // Respond within 3s, finish the DM work in the background
        _ = Task.Run(() => FinishTicketAsync(c.User, c.Client.Rest, c.Interaction, laya,
            title, description, priority ?? TicketPriority.Auto, attachment?.Url, assignee?.Id ?? itUserId));
        return (InteractionCallbackProperties)InteractionCallback.DeferredMessage(MessageFlags.Ephemeral);
    }

    return InteractionCallback.Modal(new ModalProperties("modal-it-ticket", "New IT Ticket")
    {
        new LabelProperties("Title", new TextInputProperties("title", TextInputStyle.Short)),
        new LabelProperties("Description",
            new TextInputProperties("description", TextInputStyle.Paragraph) { Required = false }),
        new LabelProperties("Priority", new StringMenuProperties("priority",
        [
            new StringMenuSelectOptionProperties("Auto (let the model decide)", "auto") { Default = true },
            new StringMenuSelectOptionProperties("Urgent", "urgent"),
            new StringMenuSelectOptionProperties("No rush", "no-rush"),
            new StringMenuSelectOptionProperties("Report", "report"),
        ]) { Required = false }),
        new LabelProperties("Attachment", new FileUploadProperties("file") { Required = false, MaxValues = 1 }),
    });
});

host.AddComponentInteraction<ModalInteractionContext>("modal-it-ticket", (ModalInteractionContext c) =>
{
    var fields = c.Components.OfType<Label>().Select(l => l.Component).ToList();
    string? Text(string id) => fields.OfType<TextInput>().FirstOrDefault(i => i.CustomId == id)?.Value;
    var fileUrl = fields.OfType<FileUpload>().FirstOrDefault(f => f.CustomId == "file")
        ?.Attachments.FirstOrDefault()?.Url;
    var priority = fields.OfType<StringMenu>().FirstOrDefault(m => m.CustomId == "priority")
        ?.SelectedValues?.FirstOrDefault() switch
    {
        "urgent" => TicketPriority.Urgent,
        "no-rush" => TicketPriority.NoRush,
        "report" => TicketPriority.Report,
        _ => TicketPriority.Auto,
    };
    _ = Task.Run(() => FinishTicketAsync(c.User, c.Client.Rest, c.Interaction, laya,
        Text("title"), Text("description"), priority, fileUrl, itUserId));
    return InteractionCallback.DeferredMessage(MessageFlags.Ephemeral);
});

// Status buttons — customId: itstatus:<Status>:<ticketId> (no dashes — they're param separators!)
host.AddComponentInteraction<ButtonInteractionContext>("itstatus",
    (ButtonInteractionContext c, string status, string ticketId) =>
{
    TicketStore.AppendStatus(c.User.ToString(), ticketId, status);
    return InteractionCallback.ModifyMessage(m =>
    {
        m.Content = Card(ticketId, $"status updated to `{status}`", liveTimer: false);
        m.Components = [FollowupRow(ticketId)];
    });
});

// Reopen — back to the full status row, timer keeps counting from creation
host.AddComponentInteraction<ButtonInteractionContext>("itreopen",
    (ButtonInteractionContext c, string ticketId) =>
{
    TicketStore.AppendStatus(c.User.ToString(), ticketId, "reopened");
    return InteractionCallback.ModifyMessage(m =>
    {
        m.Content = Card(ticketId, "`reopened`", liveTimer: true);
        m.Components = [FullRow(ticketId)];
    });
});

// Add note — small modal, appends to the log + ticket JSON
host.AddComponentInteraction<ButtonInteractionContext>("itnote",
    (ButtonInteractionContext c, string ticketId) =>
    InteractionCallback.Modal(new ModalProperties($"notemodal:{ticketId}", $"Note on ticket {ticketId}")
    {
        new LabelProperties("Follow-up note", new TextInputProperties("note", TextInputStyle.Paragraph)),
    }));

host.AddComponentInteraction<ModalInteractionContext>("notemodal",
    (ModalInteractionContext c, string ticketId) =>
{
    var note = c.Components.OfType<Label>().Select(l => l.Component)
        .OfType<TextInput>().First(i => i.CustomId == "note").Value;
    var count = TicketStore.AppendNote(c.User.ToString(), ticketId, note);
    return InteractionCallback.ModifyMessage(m =>
    {
        m.Content = Card(ticketId, $"note #{count} added", liveTimer: true) + $"\n> {note}";
        m.Components = [FollowupRow(ticketId)];
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
    string Text(string id) => fields.OfType<TextInput>().FirstOrDefault(i => i.CustomId == id)?.Value ?? "";
    var fileUrl = fields.OfType<FileUpload>().FirstOrDefault(f => f.CustomId == "reportfile")
        ?.Attachments.FirstOrDefault()?.Url;
    var anonymous = fields.OfType<Checkbox>().FirstOrDefault(f => f.CustomId == "anonymous")?.Checked ?? true;
    TicketStore.AppendReport(c.User.ToString(), ticketId, Text("complaint"), Text("action"), anonymous, fileUrl);
    TicketStore.AppendStatus(c.User.ToString(), ticketId, "complete"); // card says complete — persist it
    return InteractionCallback.ModifyMessage(m =>
    {
        m.Content = Card(ticketId, "status updated to `complete`", liveTimer: false);
        m.Components = [FollowupRow(ticketId)];
    });
});

static ActionRowProperties FullRow(string ticketId) => new()
{
    new ButtonProperties($"itstatus:cancel:{ticketId}", "Cancel", ButtonStyle.Secondary),
    new ButtonProperties($"itstatus:complete:{ticketId}", "Complete", ButtonStyle.Success),
    new ButtonProperties($"itstatus:unsolved:{ticketId}", "Unsolved", ButtonStyle.Danger),
    new ButtonProperties($"itstatus:planned:{ticketId}", "Planned", ButtonStyle.Primary),
    new ButtonProperties($"itreport:{ticketId}", "Report", ButtonStyle.Secondary),
};

static ActionRowProperties FollowupRow(string ticketId) => new()
{
    new ButtonProperties($"itreopen:{ticketId}", "Reopen", ButtonStyle.Primary),
    new ButtonProperties($"itnote:{ticketId}", "Add note", ButtonStyle.Secondary),
    new ButtonProperties($"itreport:{ticketId}", "Report", ButtonStyle.Secondary),
};

// Full card for updates — status line on top, all stored ticket data below
static string Card(string ticketId, string status, bool liveTimer)
{
    var age = TicketStore.Age(ticketId);
    var header = $"**IT ticket `{ticketId}`** — {status}";
    if (liveTimer && age is not null)
        header += $" — <t:{(DateTimeOffset.UtcNow - age.Value).ToUnixTimeSeconds()}:R>";
    else if (age is not null)
        header += $" (open for {FormatAge(age.Value)})";

    var t = TicketStore.LoadJson(ticketId);
    if (t is null) return header;
    var notes = t["notes"] as JsonArray;
    return header + "\n" + CardBody(
        t["title"]?.GetValue<string>(),
        t["description"]?.GetValue<string>(),
        t["priority"]?.GetValue<string>() ?? "urgent",
        t["auto"]?.GetValue<bool>() ?? false,
        t["offline"]?.GetValue<bool>() ?? false,
        t["file"]?.GetValue<string>(),
        t["assigned"]?.GetValue<string>(),
        notes?.Count ?? 0);
}

static string CardBody(string? title, string? desc, string? priority,
    bool auto, bool offline, string? file, string? assigned, int noteCount = 0)
{
    var body = $"Title: **{(string.IsNullOrWhiteSpace(title) ? "(no title)" : title)}**\n" +
        $"Priority: `{priority}`" +
        (auto ? " *(auto-classified)*" : offline ? " *(classifier offline — defaulted)*" : "") +
        $"\n> {(string.IsNullOrWhiteSpace(desc) ? "(no description)" : desc)}";
    if (assigned is not null) body += $"\n👤 Handled by <@{assigned}>";
    if (file is not null) body += $"\n📎 {file}";
    if (noteCount > 0) body += $"\n📝 {noteCount} note(s)";

    var solution = TicketStore.BestSolution($"{title} {desc}");
    if (solution is not null)
    {
        body += $"\n\n**💡 IT solution — {solution.Title}:**\n{solution.Text}";
        if (solution.Image is not null) body += $"\n{solution.Image}";
    }
    return body;
}

static string FormatAge(TimeSpan a) => a.TotalHours >= 1
    ? $"{(int)a.TotalHours}h {a.Minutes}m"
    : a.TotalMinutes >= 1 ? $"{(int)a.TotalMinutes}m" : $"{(int)a.TotalSeconds}s";

// Runs after the deferred ack — DMs the full card, then follows up on the interaction
static async Task FinishTicketAsync(
    User user, RestClient rest, Interaction interaction, HttpClient laya,
    string? title, string? description, TicketPriority priority, string? attachmentUrl,
    ulong? assigneeId)
{
    var resolved = TicketPriority.Urgent;
    var auto = false; var offline = false;
    if (priority == TicketPriority.Auto)
    {
        var r = await ClassifyAsync(laya, $"{title} {description}");
        if (r is null) offline = true;
        else { resolved = r.Value; auto = true; }
    }
    else resolved = priority;
    var (ticketId, content, buttons) = BuildTicket(
        user.ToString(), title, description, resolved, auto, offline, attachmentUrl, assigneeId);
    try
    {
        var dm = await user.GetDMChannelAsync();
        await rest.SendMessageAsync(dm.Id,
            new MessageProperties { Content = content, Components = [buttons] });
        await rest.SendInteractionFollowupMessageAsync(interaction.ApplicationId, interaction.Token,
            new InteractionMessageProperties
            {
                Content = $"**IT ticket `{ticketId}` created** — sent to your DMs 📬",
                Flags = MessageFlags.Ephemeral,
            });
    }
    catch
    {
        // DMs closed — fall back to an ephemeral followup with the full card
        await rest.SendInteractionFollowupMessageAsync(interaction.ApplicationId, interaction.Token,
            new InteractionMessageProperties
            {
                Content = content, Flags = MessageFlags.Ephemeral, Components = [buttons],
            });
    }

    // Notify the assignee — they get a working copy of the card in their DMs
    if (assigneeId is not null && assigneeId != user.Id)
    {
        try
        {
            var it = await rest.GetUserAsync(assigneeId.Value);
            var itDm = await it.GetDMChannelAsync();
            await rest.SendMessageAsync(itDm.Id, new MessageProperties
            {
                Content = $"**Ticket `{ticketId}` assigned to you** — from {user}\n" + content,
                Components = [buttons],
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[assign] notify failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}

static (string Id, string Content, ActionRowProperties Buttons) BuildTicket(
    string user, string? title, string? description, TicketPriority priority,
    bool auto, bool offline, string? attachmentUrl, ulong? assigneeId)
{
    var ticketId = Guid.NewGuid().ToString("N")[..8];
    TicketStore.Append(user, ticketId, PriorityName(priority), auto, offline,
        title, description, attachmentUrl, assigneeId?.ToString());
    return (ticketId, Card(ticketId, "created", liveTimer: true), FullRow(ticketId));
}

static string PriorityName(TicketPriority p) => p switch
{
    TicketPriority.Auto => "auto",
    TicketPriority.NoRush => "no-rush",
    TicketPriority.Report => "report",
    _ => "urgent",
};

// laya classifier — http://127.0.0.1:8399/classify, one retry, null = offline
static async Task<TicketPriority?> ClassifyAsync(HttpClient laya, string text)
{
    if (string.IsNullOrWhiteSpace(text)) return TicketPriority.NoRush;
    for (var attempt = 0; attempt < 2; attempt++)
    {
        try
        {
            var res = await laya.PostAsync("http://127.0.0.1:8399/classify",
                new StringContent(JsonSerializer.Serialize(new { text }),
                    Encoding.UTF8, "application/json"));
            var body = await res.Content.ReadAsStringAsync();
            if (res.IsSuccessStatusCode)
                return JsonDocument.Parse(body).RootElement.GetProperty("priority").GetString() switch
                {
                    "no-rush" => TicketPriority.NoRush,
                    "report" => TicketPriority.Report,
                    _ => TicketPriority.Urgent,
                };
            Console.WriteLine($"[laya] HTTP {(int)res.StatusCode}: {body}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[laya] classify failed: {ex.GetType().Name}: {ex.Message}");
        }
        if (attempt == 0) await Task.Delay(1500);
    }
    return null;
}



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

    public static void Append(string user, string id, string priority, bool auto, bool offline,
        string? title, string? desc, string? file, string? assigned)
    {
        var line = $"[{DateTimeOffset.UtcNow:u}] user={user} ticket={id} priority={priority}{(auto ? " auto" : "")}{(offline ? " classifier-offline" : "")} | title={Clean(title) ?? "(no title)"} | desc={Clean(desc)}";
        if (file is not null) line += $" | file={file}";
        if (assigned is not null) line += $" | assigned={assigned}";
        File.AppendAllText(FileName, line + '\n');

        Directory.CreateDirectory(Dir);
        File.WriteAllText($"{Dir}/{Slug(title)}-{id}.json",
            JsonSerializer.Serialize(new
            {
                id, user,
                created = DateTimeOffset.UtcNow.ToString("u"),
                priority,
                auto,
                offline,
                assigned,
                title,
                description = desc,
                file,
                status = "open",
            }, JsonOpts));
    }

    public static JsonNode? LoadJson(string id)
    {
        if (!Directory.Exists(Dir)) return null;
        var path = Directory.EnumerateFiles(Dir, $"*-{id}.json").FirstOrDefault();
        if (path is null) return null;
        try { return JsonNode.Parse(File.ReadAllText(path)); }
        catch { return null; }
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

    public static void AppendStatus(string user, string id, string status)
    {
        File.AppendAllText(FileName, $"[{DateTimeOffset.UtcNow:u}] user={user} ticket={id} status={status}\n");
        UpdateJson(id, n => n["status"] = status);
    }

    // Returns this ticket's note count after appending
    public static int AppendNote(string user, string id, string note)
    {
        File.AppendAllText(FileName,
            $"[{DateTimeOffset.UtcNow:u}] user={Clean(user)} ticket={id} | note={Clean(note)}\n");
        UpdateJson(id, n =>
        {
            if (n["notes"] is not JsonArray notes) n["notes"] = notes = new JsonArray();
            notes.Add(note);
        });
        return File.ReadLines(FileName)
            .Count(l => l.Contains($"ticket={id} ") && l.Contains("| note="));
    }

    private static void UpdateJson(string id, Action<JsonNode> update)
    {
        if (!Directory.Exists(Dir)) return;
        var path = Directory.EnumerateFiles(Dir, $"*-{id}.json").FirstOrDefault();
        if (path is null) return;
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path))!;
            update(node);
            File.WriteAllText(path, node.ToJsonString(JsonOpts));
        }
        catch { }
    }

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

sealed class WorldTicker(FrentWorldLoop world) : BackgroundService
{
    // 20 ticks per second
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        world.RunAsync(TimeSpan.FromMilliseconds(50), stoppingToken);
}

public enum TicketPriority
{
    [SlashCommandChoice(Name = "auto")] Auto,
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
