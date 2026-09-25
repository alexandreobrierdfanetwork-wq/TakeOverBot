using Discord;
using Discord.WebSocket;
using TakeOverBot.Interfaces;
using TakeOverBot.Services;

namespace TakeOverBot.Commands;

/// <summary>
/// Relance manuellement les membres qui voient le salon et n'ont pas encore voté.
/// </summary>
public class VoteRemindCommand(VoteService voteService) : ISlashCommand
{
    public string Name => "rappelvote";
    public string Icon => "📣";
    public string Description => "Relance les membres du salon qui n'ont pas encore voté";
    public string[] AllowedRoleIds => ["DISCORD_IDS_ROLES_ADMIN", "DISCORD_IDS_ROLES_STAFF"];

    public ISlashCommandOption[] Options =>
    [
        new SlashCommandOption(
            "message_id",
            "ID du message du sondage (sinon le dernier sondage actif du salon)",
            ApplicationCommandOptionType.String,
            IsRequired: false
        )
    ];

    public async Task ExecuteAsync(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);

        if (command.Channel is not IMessageChannel channel)
        {
            await command.FollowupAsync("❌ Cette commande doit être utilisée dans un canal textuel.", ephemeral: true);
            return;
        }

        var guildUser = command.User as SocketGuildUser;
        var guild = guildUser!.Guild;

        ulong? messageId = null;
        if (command.Data.Options.FirstOrDefault(o => o.Name == "message_id")?.Value is string messageIdRaw)
        {
            if (!ulong.TryParse(messageIdRaw, out var parsed))
            {
                await command.FollowupAsync("❌ L'ID du message est invalide.", ephemeral: true);
                return;
            }

            messageId = parsed;
        }

        var poll = await voteService.ResolvePollAsync(guild, channel, messageId);
        if (poll is null)
        {
            await command.FollowupAsync(
                messageId.HasValue
                    ? "❌ Aucun sondage actif avec cet ID sur le serveur."
                    : "❌ Aucun sondage actif dans ce salon.",
                ephemeral: true
            );
            return;
        }

        var result = await voteService.RemindPollAsync(poll, RemindKind.Manual);
        if (!result.Success)
        {
            await command.FollowupAsync($"❌ {result.Message}", ephemeral: true);
            return;
        }

        await command.FollowupAsync($"✅ {result.Message}", ephemeral: true);
    }
}
