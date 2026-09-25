using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using TakeOverBot.Interfaces;
using TakeOverBot.Models;
using TakeOverBot.Services;

namespace TakeOverBot.Commands;

/// <summary>
/// Create vote command aims to create a poll in a given channel.
/// This command is restricted to administrators or staff.
/// This command will also save the poll in the database for later relance.
/// </summary>
/// <param name="scopeFactory">Used to get DbContext</param>
public class CreateVoteCommand(IServiceScopeFactory scopeFactory, VoteService voteService) : ISlashCommand
{
    public string Name => "vote";
    public string Icon => "🗳️";
    public string Description => "Crée un sondage Discord dans un channel";
    public string[] AllowedRoleIds => ["DISCORD_IDS_ROLES_ADMIN", "DISCORD_IDS_ROLES_STAFF"];

    public ISlashCommandOption[] Options =>
    [
        new SlashCommandOption(
            Name: "cible",
            Description: "Qui contacter ?",
            Type: ApplicationCommandOptionType.String,
            IsRequired: true,
            Choices:
            [
                ("Staff", "staff"),
                ("Admin", "admin")
            ]
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

        var staffRoleId = Environment.GetEnvironmentVariable("DISCORD_IDS_ROLES_STAFF");
        var adminRoleId = Environment.GetEnvironmentVariable("DISCORD_IDS_ROLES_ADMIN");
        var guildUser = command.User as SocketGuildUser;

        var isAdmin = guildUser!.Roles.Any(r => r.Id.ToString() == adminRoleId);

        var options = command.Data.Options.ToDictionary(o => o.Name, o => o.Value);

        var cible = options["cible"] as string;

        if (cible == "admin" && !isAdmin)
        {
            await command.FollowupAsync("❌ Seuls les admins peuvent créer un sondage à destination des admins.", ephemeral: true);
            return;
        }

        var question = options["question"] as string;
        var choixRaw = options["choix"] as string ?? string.Empty;
        var duree = options.TryGetValue("duree", out var d) ? Convert.ToUInt32(d) : 24u;
        var multiselect = options.TryGetValue("multiselect", out var m) && (bool)m;

        var answers = choixRaw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(10) // Select only the first 10 choices
            .Select(c => new PollMediaProperties { Text = c })
            .ToList();

        if (answers.Count < 2)
        {
            await command.FollowupAsync("❌ Il faut au minimum **2 choix** séparés par des virgules.", ephemeral: true);
            return;
        }

        duree = Math.Clamp(duree, 1, 768);

        var targetRoleIdString = cible == "admin" ? adminRoleId : staffRoleId;
        var targetRoleId = ulong.Parse(targetRoleIdString ?? "0");

        var voteChannelEnvKey = cible == "admin" ? "DISCORD_IDS_CHANNELS_VOTE_ADMIN" : "DISCORD_IDS_CHANNELS_VOTE_STAFF";
        var voteChannelId = ulong.Parse(Environment.GetEnvironmentVariable(voteChannelEnvKey) ?? "0");

        var guild = guildUser.Guild;
        var targetRole = guild.GetRole(targetRoleId);
        var channel = guild.GetTextChannel(voteChannelId);

        var poll = new PollProperties
        {
            Question = new PollMediaProperties { Text = question },
            Answers = answers,
            Duration = duree,
            AllowMultiselect = multiselect,
            LayoutType = PollLayout.Default
        };

        await channel.SendMessageAsync(text: targetRole!.Mention, poll: poll);

        var sentMessages = await channel.GetMessagesAsync(1).FlattenAsync();
        var sentMessage = sentMessages.First();

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

        await command.FollowupAsync($"✅ Sondage créé avec **{answers.Count} choix** dans {channel.Mention} !", ephemeral: true);
    }
}