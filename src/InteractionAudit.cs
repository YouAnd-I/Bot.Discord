using NetCord;
using NetCord.Hosting.Gateway;
using Ticket.Adapter.Npgsql;

// The audit side of Ticket.Adapter.Npgsql: every Discord interaction the bot
// receives — every slash command, button, select menu, modal and context menu,
// whatever feature handles it — becomes one row in the interaction table, with
// its options in interaction_option.
// NetCord's AddGatewayHandlers only discovers public classes — keep it public.
public sealed class InteractionAuditHandler(NpgsqlInteractions log) : IInteractionCreateGatewayHandler
{
    public async ValueTask HandleAsync(Interaction interaction)
    {
        try
        {
            await log.RecordAsync(Describe(interaction)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[audit] failed to log interaction: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static InteractionEntry Describe(Interaction i)
    {
        var (kind, name, options) = KindOf(i);

        return new InteractionEntry(
            InteractionId: i.Id,
            Kind: kind,
            Name: name,
            TicketId: TicketIdOf(name),
            UserId: i.User?.Id,
            UserName: i.User?.Username,
            ChannelId: i.Channel?.Id,
            GuildId: i.GuildId,
            Options: options,
            ReceivedAtUtc: DateTimeOffset.UtcNow);
    }

    private static (string Kind, string Name, IReadOnlyList<InteractionOption>? Options) KindOf(Interaction i) =>
        i switch
        {
            SlashCommandInteraction slash => ("slash", slash.Data.Name,
                [.. slash.Data.Options.Select(o => new InteractionOption(o.Name, o.Value?.ToString()))]),

            UserCommandInteraction user => ("user-command", user.Data.Name, null),

            MessageCommandInteraction message => ("message-command", message.Data.Name, null),

            ModalInteraction modal => ("modal", modal.Data.CustomId,
                [.. modal.Data.Components.OfType<Label>()
                    .Select(l => l.Component).OfType<TextInput>()
                    .Select((input, n) => new InteractionOption($"field{n + 1}", input.Value))]),

            StringMenuInteraction menu => ("component", menu.Data.CustomId,
                [.. menu.Data.SelectedValues.Select(v => new InteractionOption("value", v))]),

            ComponentInteraction component => ("component", component.Data.CustomId, null),

            _ => ("other", i.GetType().Name, null),
        };

    // The IT card's custom ids all end in the ticket they belong to.
    private static string? TicketIdOf(string name) => name.Split(':') switch
    {
        ["itstatus", _, var id] => id,
        ["itreopen", var id] => id,
        ["itnote", var id] => id,
        ["itreport", var id] => id,
        ["notemodal", var id] => id,
        ["reportmodal", var id] => id,
        _ => null,
    };
}
