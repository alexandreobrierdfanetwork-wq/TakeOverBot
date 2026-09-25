using Discord;
using Discord.WebSocket;
using TakeOverBot.Interfaces;
using TakeOverBot.Services;

namespace TakeOverBot.Commands;

/// <summary>
/// Réinitialise la période du rapport de participation aux votes.
/// </summary>
public class ResetParticipationCommand(VoteRecapService voteRecapService) : ISlashCommand
{
    public string Name => "resetparticipation";
    public string Icon => "🔄";
    public string Description => "Réinitialise les compteurs du rapport de participation aux votes";
    public string[] AllowedRoleIds => ["DISCORD_IDS_ROLES_ADMIN"];

    public ISlashCommandOption[] Options =>
    [
        new SlashCommandOption(
            "confirmer",
            "Confirmer la réinitialisation (obligatoire)",
            ApplicationCommandOptionType.Boolean
        )
    ];

    public async Task ExecuteAsync(SocketSlashCommand command)
    {
        await command.DeferAsync(ephemeral: true);

        var confirmer = command.Data.Options.FirstOrDefault(o => o.Name == "confirmer")?.Value is true;
        if (!confirmer)
        {
            await command.FollowupAsync(
                "❌ Passe `confirmer: true` pour réinitialiser la période du rapport.",
                ephemeral: true);
            return;
        }

        var guildUser = command.User as SocketGuildUser;
        await voteRecapService.ResetPeriodAsync(guildUser!.Guild);

        await command.FollowupAsync(
            "✅ Période du rapport réinitialisée. Le message dans le salon recap a été mis à jour.",
            ephemeral: true);
    }
}
