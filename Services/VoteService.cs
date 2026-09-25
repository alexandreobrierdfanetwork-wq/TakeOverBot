using Discord;
using Discord.Net;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TakeOverBot.Models;

namespace TakeOverBot.Services;

public enum RemindKind
{
    Before72h,
    Before24h,
    Before3h,
    Manual,
    Expired
}

public record VoteRemindResult(bool Success, string Message, int NonVoterCount = 0, int DmFailedCount = 0)
{
    public static VoteRemindResult Ok(int nonVoterCount, int dmFailedCount = 0)
    {
        if (nonVoterCount == 0)
            return new(true, "Tous les membres ont voté.", 0, dmFailedCount);

        var text = $"{nonVoterCount} membre{(nonVoterCount > 1 ? "s" : "")} relancé{(nonVoterCount > 1 ? "s" : "")} (salon + DM)";
        if (dmFailedCount > 0)
            text += $", {dmFailedCount} DM refusé{(dmFailedCount > 1 ? "s" : "")}";

        return new(true, text + ".", nonVoterCount, dmFailedCount);
    }

    public static VoteRemindResult Fail(string message) => new(false, message);
}

public class VoteService(IServiceScopeFactory scopeFactory, DiscordSocketClient discordClient)
{
    public const ulong KnownPollGuildId = 1192249336885678130;
    public const ulong KnownPollChannelId = 1202728633555353600;
    public const ulong KnownPollMessageId = 1551507073773076540;

    private const int MentionsPerMessage = 35;
    private const int DiscoverMessageLimit = 100;
    private const long Seconds72h = 72 * 3600;
    private const long Seconds24h = 24 * 3600;
    private const long Seconds3h = 3 * 3600;

    public async Task StartAsync()
    {
        discordClient.MessageReceived += OnMessageReceived;

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        var ticks = 0;
        while (await timer.WaitForNextTickAsync())
        {
            await CheckPollsAsync();
            ticks++;
            if (ticks % 10 == 0)
                await DiscoverAllGuildPollsAsync();
        }
    }

    public async Task OnReadyAsync()
    {
        await ImportKnownPollAsync();
        await DiscoverAllGuildPollsAsync();
        await CheckPollsAsync();
    }

    public async Task<VotePoll?> FindActivePollAsync(ulong guildId, ulong? channelId = null, ulong? messageId = null)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var query = dbContext.VotePolls
            .Where(p => p.GuildId == guildId && p.ExpiresAt > now);

        if (channelId.HasValue)
            query = query.Where(p => p.ChannelId == channelId.Value);

        if (messageId.HasValue)
            query = query.Where(p => p.MessageId == messageId.Value);

        return await query
            .OrderByDescending(p => p.Id)
            .FirstOrDefaultAsync();
    }

    public async Task<VotePoll?> ResolvePollAsync(SocketGuild guild, SocketTextChannel channel, ulong? messageId)
    {
        if (messageId.HasValue)
        {
            var existing = await FindActivePollAsync(guild.Id, messageId: messageId);
            if (existing is not null)
                return existing;

            var fetched = await FetchPollMessageAsync(guild, channel, messageId.Value);
            if (fetched is not null)
                return await UpsertPollFromMessageAsync(fetched);
        }
        else
        {
            await DiscoverChannelPollsAsync(channel);
            return await FindActivePollAsync(guild.Id, channel.Id);
        }

        return null;
    }

    public async Task<VotePoll?> UpsertPollFromMessageAsync(IUserMessage message, ulong targetRoleId = 0)
    {
        if (message.Poll is not { } discordPoll)
            return null;

        var expiresAt = discordPoll.ExpiresAt.ToUnixTimeSeconds();
        if (expiresAt <= 0)
            return null;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (expiresAt <= now)
            return null;

        var guildId = TryGetGuildId(message);
        if (guildId == 0)
            return null;

        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var poll = await dbContext.VotePolls
            .FirstOrDefaultAsync(p => p.MessageId == message.Id);

        if (poll is null)
        {
            poll = new VotePoll
            {
                GuildId = guildId,
                ChannelId = message.Channel.Id,
                MessageId = message.Id,
                TargetRoleId = targetRoleId,
                CreatedAt = message.Timestamp.ToUnixTimeSeconds(),
                ExpiresAt = expiresAt
            };
            dbContext.VotePolls.Add(poll);
        }
        else
        {
            poll.ChannelId = message.Channel.Id;
            poll.GuildId = guildId;
            poll.ExpiresAt = expiresAt;
            if (targetRoleId != 0)
                poll.TargetRoleId = targetRoleId;
            if (poll.CreatedAt == 0)
                poll.CreatedAt = message.Timestamp.ToUnixTimeSeconds();
        }

        await dbContext.SaveChangesAsync();
        return poll;
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

            var nonVoters = await GetNonVoterIdsAsync(guild, channel, message, discordPoll);
            var question = discordPoll.Question.Text ?? "sondage";
            var jumpUrl = $"https://discord.com/channels/{poll.GuildId}/{poll.ChannelId}/{poll.MessageId}";

            if (kind == RemindKind.Expired)
            {
                await channel.SendMessageAsync("⏰ **Fin du vote !**");
                return VoteRemindResult.Ok(0);
            }

            var header = HeaderFor(kind);
            if (nonVoters.Count == 0)
                return VoteRemindResult.Ok(0);

            await SendChannelMentionsAsync(channel, nonVoters, header);
            var dmFailed = await SendDirectMessagesAsync(nonVoters, question, jumpUrl);
            return VoteRemindResult.Ok(nonVoters.Count, dmFailed);
        }
        catch (Exception ex)
        {
            await LogErrorAsync(guild, $"Erreur sur le sondage {poll.Id}", ex);
            return VoteRemindResult.Fail($"Erreur lors du rappel : {ex.Message}");
        }
    }

    private async Task OnMessageReceived(SocketMessage message)
    {
        if (message.Author.IsBot)
            return;

        if (message is not IUserMessage userMessage || userMessage.Poll is null)
            return;

        try
        {
            await UpsertPollFromMessageAsync(userMessage);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VoteService] Upsert message reçu : {ex.Message}");
        }
    }

    private async Task ImportKnownPollAsync()
    {
        try
        {
            var guild = discordClient.GetGuild(KnownPollGuildId);
            var channel = guild?.GetTextChannel(KnownPollChannelId);
            if (channel is null)
                return;

            if (await channel.GetMessageAsync(KnownPollMessageId) is IUserMessage message)
                await UpsertPollFromMessageAsync(message);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VoteService] Import sondage connu : {ex.Message}");
        }
    }

    private async Task DiscoverAllGuildPollsAsync()
    {
        foreach (var guild in discordClient.Guilds)
            await DiscoverGuildPollsAsync(guild);
    }

    private async Task DiscoverGuildPollsAsync(SocketGuild guild)
    {
        foreach (var channel in guild.TextChannels)
        {
            try
            {
                await DiscoverChannelPollsAsync(channel);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VoteService] Scan {channel.Name} : {ex.Message}");
            }
        }
    }

    private async Task DiscoverChannelPollsAsync(SocketTextChannel channel)
    {
        await foreach (var batch in channel.GetMessagesAsync(DiscoverMessageLimit))
        {
            foreach (var msg in batch)
            {
                if (msg is IUserMessage userMessage && userMessage.Poll is not null)
                    await UpsertPollFromMessageAsync(userMessage);
            }
        }
    }

    private async Task<IUserMessage?> FetchPollMessageAsync(SocketGuild guild, SocketTextChannel currentChannel, ulong messageId)
    {
        if (await currentChannel.GetMessageAsync(messageId) is IUserMessage inCurrent)
            return inCurrent;

        foreach (var channel in guild.TextChannels)
        {
            if (channel.Id == currentChannel.Id)
                continue;

            try
            {
                if (await channel.GetMessageAsync(messageId) is IUserMessage found)
                    return found;
            }
            catch
            {
                // Salon inaccessible
            }
        }

        return null;
    }

    private async Task CheckPollsAsync()
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var active = await dbContext.VotePolls
            .Where(p => p.ExpiresAt > now)
            .ToListAsync();

        foreach (var poll in active)
        {
            var duration = poll.ExpiresAt - poll.CreatedAt;
            var kind = NextAutoRemindKind(poll, duration, now);
            if (kind is null)
                continue;

            await RemindPollAsync(poll, kind.Value);
            MarkThresholdSent(poll, kind.Value);
        }

        var expired = await dbContext.VotePolls
            .Where(p => p.ExpiresAt <= now)
            .ToListAsync();

        foreach (var poll in expired)
            await RemindPollAsync(poll, RemindKind.Expired);

        dbContext.VotePolls.RemoveRange(expired);
        await dbContext.SaveChangesAsync();
    }

    private static RemindKind? NextAutoRemindKind(VotePoll poll, long duration, long now)
    {
        if (duration >= Seconds3h && now >= poll.ExpiresAt - Seconds3h && !poll.Remind3Sent)
            return RemindKind.Before3h;

        if (duration >= Seconds24h && now >= poll.ExpiresAt - Seconds24h && !poll.Remind24Sent)
            return RemindKind.Before24h;

        if (duration >= Seconds72h && now >= poll.ExpiresAt - Seconds72h && !poll.Remind72Sent)
            return RemindKind.Before72h;

        return null;
    }

    private static void MarkThresholdSent(VotePoll poll, RemindKind kind)
    {
        switch (kind)
        {
            case RemindKind.Before3h:
                poll.Remind3Sent = true;
                poll.Remind24Sent = true;
                poll.Remind72Sent = true;
                break;
            case RemindKind.Before24h:
                poll.Remind24Sent = true;
                poll.Remind72Sent = true;
                break;
            case RemindKind.Before72h:
                poll.Remind72Sent = true;
                break;
        }
    }

    private async Task<List<ulong>> GetNonVoterIdsAsync(
        SocketGuild guild,
        SocketTextChannel channel,
        IUserMessage message,
        Poll discordPoll)
    {
        await guild.DownloadUsersAsync();

        var viewers = guild.Users
            .Where(u => !u.IsBot && u.GetPermissions(channel).ViewChannel)
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

        return viewers.Except(voterIds).ToList();
    }

    private static async Task SendChannelMentionsAsync(
        IMessageChannel channel,
        IReadOnlyList<ulong> nonVoters,
        string header)
    {
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

    private async Task<int> SendDirectMessagesAsync(
        IReadOnlyList<ulong> nonVoters,
        string question,
        string jumpUrl)
    {
        var failed = 0;
        var body =
            $"Salut ! Tu n'as pas encore voté au sondage **{question}**.\n" +
            $"Merci de répondre ici : {jumpUrl}";

        foreach (var userId in nonVoters)
        {
            try
            {
                IUser? user = discordClient.GetUser(userId);
                user ??= await discordClient.Rest.GetUserAsync(userId);
                if (user is null)
                {
                    failed++;
                    continue;
                }

                var dm = await user.CreateDMChannelAsync();
                await dm.SendMessageAsync(body);
            }
            catch (HttpException ex) when (ex.DiscordCode == DiscordErrorCode.CannotSendMessageToUser)
            {
                failed++;
            }
            catch
            {
                failed++;
            }

            await Task.Delay(400);
        }

        return failed;
    }

    private static string HeaderFor(RemindKind kind) => kind switch
    {
        RemindKind.Before72h => "⏰ **Rappel de vote (72 h restantes) !**\nLes membres suivants n'ont pas encore voté :",
        RemindKind.Before24h => "⏰ **Rappel de vote (24 h restantes) !**\nLes membres suivants n'ont pas encore voté :",
        RemindKind.Before3h => "⏰ **Rappel de vote (3 h restantes) !**\nLes membres suivants n'ont pas encore voté :",
        _ => "⏰ **Rappel de vote !**\nLes membres suivants n'ont pas encore voté :"
    };

    private static ulong TryGetGuildId(IUserMessage message) =>
        message.Channel is SocketGuildChannel guildChannel
            ? guildChannel.Guild.Id
            : message.Channel is IGuildChannel restGuildChannel
                ? restGuildChannel.GuildId
                : 0;

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
