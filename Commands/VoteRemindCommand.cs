using Discord;
using Discord.WebSocket;
using TakeOverBot.Interfaces;
using TakeOverBot.Services;

namespace TakeOverBot.Commands;

/// <summary>
/// Relance manuellement les membres du rôle cible qui n'ont pas encore voté.
/// </summary>
public class VoteRemindCommand(VoteService voteService) : ISlashCommand
{
    public string Name => "voterappel";
    public string Icon => "📣";
    public string Description => "Relance les membres qui n'ont pas encore voté";
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

        if (command.Channel is not SocketTextChannel channel)
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

        var poll = await voteService.FindActivePollAsync(guild.Id, channel.Id, messageId);
        if (poll is null)
        {
            await command.FollowupAsync(
                messageId.HasValue
                    ? "❌ Aucun sondage actif avec cet ID dans ce salon."
                    : "❌ Aucun sondage actif dans ce salon.",
                ephemeral: true
            );
            return;
        }

        var adminRoleId = Environment.GetEnvironmentVariable("DISCORD_IDS_ROLES_ADMIN");
        var isAdmin = guildUser.Roles.Any(r => r.Id.ToString() == adminRoleId);
        if (adminRoleId is not null && poll.TargetRoleId.ToString() == adminRoleId && !isAdmin)
        {
            await command.FollowupAsync("❌ Seuls les admins peuvent relancer un sondage à destination des admins.", ephemeral: true);
            return;
        }

        var voteStaffId = Environment.GetEnvironmentVariable("DISCORD_IDS_CHANNELS_VOTE_STAFF");
        var voteAdminId = Environment.GetEnvironmentVariable("DISCORD_IDS_CHANNELS_VOTE_ADMIN");
        var isVoteChannel = channel.Id.ToString() == voteStaffId || channel.Id.ToString() == voteAdminId;

        var result = await voteService.RemindPollAsync(poll, RemindKind.Manual);
        var warning = isVoteChannel ? "" : "\n⚠️ Cette commande est prévue pour les salons de vote.";

        if (!result.Success)
        {
            await command.FollowupAsync($"❌ {result.Message}{warning}", ephemeral: true);
            return;
        }

        await command.FollowupAsync($"✅ {result.Message}{warning}", ephemeral: true);
    }
}
