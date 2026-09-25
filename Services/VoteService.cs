using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TakeOverBot.Models;

namespace TakeOverBot.Services;

public enum RemindKind
{
    Mid,
    Expired,
    Manual
}

public record VoteRemindResult(bool Success, string Message, int NonVoterCount = 0)
{
    public static VoteRemindResult Ok(int nonVoterCount) =>
        new(
            true,
            nonVoterCount == 0
                ? "Tous les membres ont voté."
                : $"{nonVoterCount} membre{(nonVoterCount > 1 ? "s" : "")} relancé{(nonVoterCount > 1 ? "s" : "")}.",
            nonVoterCount
        );

    public static VoteRemindResult Fail(string message) => new(false, message);
}

public class VoteService(IServiceScopeFactory scopeFactory, DiscordSocketClient discordClient)
{
    private const int MentionsPerMessage = 35;

    public async Task StartAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync())
        {
            await CheckPollsAsync();
        }
    }

    public async Task<VotePoll?> FindActivePollAsync(ulong guildId, ulong channelId, ulong? messageId = null)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var query = dbContext.VotePolls
            .Where(p => p.GuildId == guildId && p.ChannelId == channelId && p.ExpiresAt > now);

        if (messageId.HasValue)
            query = query.Where(p => p.MessageId == messageId.Value);

        return await query
            .OrderByDescending(p => p.Id)
            .FirstOrDefaultAsync();
    }

    public async Task<VoteRemindResult> RemindActivePollInChannelAsync(
        SocketGuild guild,
        ulong channelId,
        ulong? messageId = null)
    {
        var poll = await FindActivePollAsync(guild.Id, channelId, messageId);
        if (poll is null)
        {
            return VoteRemindResult.Fail(
                messageId.HasValue
                    ? "Aucun sondage actif avec cet ID dans ce salon."
                    : "Aucun sondage actif dans ce salon."
            );
        }

        return await RemindPollAsync(poll, RemindKind.Manual);
    }

    public async Task<VoteRemindResult> RemindPollAsync(VotePoll poll, RemindKind kind)
    {
        SocketGuild? guild = null;

        try
        {
            guild = discordClient.GetGuild(poll.GuildId);
            if (guild is null)
                return await FailAsync(null, $"Serveur introuvable pour le sondage {poll.Id}.");

            var channel = guild.GetTextChannel(poll.ChannelId);
            if (channel is null)
                return await FailAsync(guild, $"Salon introuvable pour le sondage {poll.Id}.");

            var message = await channel.GetMessageAsync(poll.MessageId) as IUserMessage;
            if (message is null)
                return await FailAsync(guild, $"Message du sondage introuvable (poll {poll.Id}).");

            if (message.Poll is not { } discordPoll)
                return await FailAsync(guild, $"Le message {poll.MessageId} n'est plus un sondage Discord (poll {poll.Id}).");

            var nonVoters = await GetNonVoterIdsAsync(guild, message, discordPoll, poll.TargetRoleId);

            var header = kind switch
            {
                RemindKind.Expired when nonVoters.Count > 0 =>
                    "⏰ **Fin du vote !**\nLes membres suivants n'ont pas voté :",
                RemindKind.Expired =>
                    "⏰ **Fin du vote !**\nTous les membres ont voté.",
                RemindKind.Mid or RemindKind.Manual when nonVoters.Count > 0 =>
                    "⏰ **Rappel de vote !**\nLes membres suivants n'ont pas encore voté :",
                _ => null
            };

            if (header is null)
                return VoteRemindResult.Ok(0);

            await SendNonVoterRemindersAsync(channel, nonVoters, header);
            return VoteRemindResult.Ok(nonVoters.Count);
        }
        catch (Exception ex)
        {
            await LogErrorAsync(guild, $"Erreur sur le sondage {poll.Id}", ex);
            return VoteRemindResult.Fail($"Erreur lors du rappel : {ex.Message}");
        }
    }

    private async Task CheckPollsAsync()
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var pollsDue = await dbContext.VotePolls
            .Where(p => !p.ReminderSent && p.RemindAt <= now && p.ExpiresAt > now)
            .ToListAsync();

        foreach (var poll in pollsDue)
        {
            await RemindPollAsync(poll, RemindKind.Mid);
            poll.ReminderSent = true;
        }

        var expired = await dbContext.VotePolls
            .Where(p => p.ExpiresAt <= now)
            .ToListAsync();

        foreach (var poll in expired)
        {
            await RemindPollAsync(poll, RemindKind.Expired);
        }

        dbContext.VotePolls.RemoveRange(expired);

        await dbContext.SaveChangesAsync();
    }

    private async Task<List<ulong>> GetNonVoterIdsAsync(
        SocketGuild guild,
        IUserMessage message,
        Poll discordPoll,
        ulong targetRoleId)
    {
        await guild.DownloadUsersAsync();

        var roleMembers = guild.Users
            .Where(u => !u.IsBot && u.Roles.Any(r => r.Id == targetRoleId))
            .Select(u => u.Id)
            .ToHashSet();

        var voterIds = new HashSet<ulong>();
        foreach (var answer in discordPoll.Answers)
        {
            var voters = await message
                .GetPollAnswerVotersAsync(answer.AnswerId)
                .FlattenAsync();

            foreach (var voter in voters)
                voterIds.Add(voter.Id);
        }

        return roleMembers.Except(voterIds).ToList();
    }

    private static async Task SendNonVoterRemindersAsync(
        IMessageChannel channel,
        IReadOnlyList<ulong> nonVoters,
        string header)
    {
        if (nonVoters.Count == 0)
        {
            await channel.SendMessageAsync(header);
            return;
        }

        for (var i = 0; i < nonVoters.Count; i += MentionsPerMessage)
        {
            var chunk = nonVoters.Skip(i).Take(MentionsPerMessage).ToList();
            var mentions = string.Join(" ", chunk.Select(id => $"<@{id}>"));
            var text = i == 0 ? $"{header}\n{mentions}" : mentions;

            var allowedMentions = new AllowedMentions(AllowedMentionTypes.None)
            {
                UserIds = chunk
            };

            await channel.SendMessageAsync(text, allowedMentions: allowedMentions);
        }
    }

    private async Task<VoteRemindResult> FailAsync(SocketGuild? guild, string message)
    {
        await LogErrorAsync(guild, message);
        return VoteRemindResult.Fail(message);
    }

    private static async Task LogErrorAsync(SocketGuild? guild, string message, Exception? ex = null)
    {
        var logLine = ex is null
            ? $"[VoteService] {message}"
            : $"[VoteService] {message} : {ex.Message}";
        Console.WriteLine(logLine);

        if (ex is not null)
            SentrySdk.CaptureException(ex);

        if (guild is null)
            return;

        var logChannelId = ulong.Parse(Environment.GetEnvironmentVariable("DISCORD_IDS_CHANNELS_LOGS") ?? "0");
        if (logChannelId == 0)
            return;

        if (guild.GetTextChannel(logChannelId) is ISocketMessageChannel logChannel)
            await logChannel.SendMessageAsync($"❌ {message}");
    }
}
