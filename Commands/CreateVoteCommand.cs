using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using TakeOverBot.Interfaces;
using TakeOverBot.Models;
using TakeOverBot.Services;

namespace TakeOverBot.Commands;

/// <summary>
/// Create vote command aims to create a poll in the current channel,
/// mentioning a role the bot is allowed to ping.
/// </summary>
public class CreateVoteCommand(IServiceScopeFactory scopeFactory, VoteService voteService) : ISlashCommand
{
    public string Name => "vote";
    public string Icon => "🗳️";
    public string Description => "Crée un sondage Discord dans le salon actuel";
    public string[] AllowedRoleIds => ["DISCORD_IDS_ROLES_ADMIN", "DISCORD_IDS_ROLES_STAFF"];

    public ISlashCommandOption[] Options =>
    [
        new SlashCommandOption(
            Name: "role",
            Description: "Rôle à mentionner (parmi ceux que le bot peut ping)",
            Type: ApplicationCommandOptionType.Role
        ),
        new SlashCommandOption(
            "question",
            "Question du sondage",
            ApplicationCommandOptionType.String
        ),
        new SlashCommandOption(
            "choix",
            "Choix séparés par des virgules (ex: Oui, Non, Peut-être)",
            ApplicationCommandOptionType.String
        ),
        new SlashCommandOption(
            "duree",
            "Durée du sondage en heures (défaut : 24, max : 768)",
            ApplicationCommandOptionType.Integer,
            IsRequired: false
        ),
        new SlashCommandOption(
            "multiselect",
            "Autoriser plusieurs réponses ? (défaut : non)",
            ApplicationCommandOptionType.Boolean,
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
        var options = command.Data.Options.ToDictionary(o => o.Name, o => o.Value);

        if (options["role"] is not SocketRole targetRole)
        {
            await command.FollowupAsync("❌ Rôle invalide.", ephemeral: true);
            return;
        }

        if (!CanBotMention(guild, targetRole, out var reason))
        {
            await command.FollowupAsync($"❌ {reason}", ephemeral: true);
            return;
        }

        var question = options["question"] as string;
        var choixRaw = options["choix"] as string ?? string.Empty;
        var duree = options.TryGetValue("duree", out var d) ? Convert.ToUInt32(d) : 24u;
        var multiselect = options.TryGetValue("multiselect", out var m) && (bool)m;

        var answers = choixRaw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(10)
            .Select(c => new PollMediaProperties { Text = c })
            .ToList();

        if (answers.Count < 2)
        {
            await command.FollowupAsync("❌ Il faut au minimum **2 choix** séparés par des virgules.", ephemeral: true);
            return;
        }

        duree = Math.Clamp(duree, 1, 768);

        var poll = new PollProperties
        {
            Question = new PollMediaProperties { Text = question },
            Answers = answers,
            Duration = duree,
            AllowMultiselect = multiselect,
            LayoutType = PollLayout.Default
        };

        var allowedMentions = new AllowedMentions(AllowedMentionTypes.None)
        {
            RoleIds = [targetRole.Id]
        };

        var sentMessage = await channel.SendMessageAsync(
            text: targetRole.Mention,
            poll: poll,
            allowedMentions: allowedMentions);

        if (sentMessage is IUserMessage userMessage)
        {
            await voteService.UpsertPollFromMessageAsync(userMessage, targetRole.Id);
        }
        else
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            dbContext.VotePolls.Add(new VotePoll
            {
                GuildId = guild.Id,
                ChannelId = channel.Id,
                MessageId = sentMessage.Id,
                TargetRoleId = targetRole.Id,
                CreatedAt = now,
                ExpiresAt = now + duree * 3600
            });

            await dbContext.SaveChangesAsync();
        }

        await command.FollowupAsync(
            $"✅ Sondage créé avec **{answers.Count} choix** pour {targetRole.Mention} dans <#{channel.Id}> !",
            ephemeral: true);
    }

    private static bool CanBotMention(SocketGuild guild, SocketRole role, out string reason)
    {
        if (role.IsEveryone)
        {
            reason = "Le rôle @everyone n'est pas autorisé.";
            return false;
        }

        var bot = guild.CurrentUser;
        if (bot is null)
        {
            reason = "Impossible de vérifier les permissions du bot.";
            return false;
        }

        if (role.Position >= bot.Hierarchy)
        {
            reason = $"Je ne peux pas mentionner le rôle **{role.Name}** (rôle trop élevé par rapport au bot).";
            return false;
        }

        if (role.IsManaged && !role.IsMentionable && !bot.GuildPermissions.MentionEveryone)
        {
            reason = $"Je ne peux pas mentionner le rôle géré **{role.Name}**.";
            return false;
        }

        reason = string.Empty;
        return true;
    }
}
