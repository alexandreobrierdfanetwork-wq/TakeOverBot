using System.Globalization;
using System.Text;
using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TakeOverBot.Models;

namespace TakeOverBot.Services;

public enum VoteRecapRecordResult
{
    Skipped,
    Recorded,
    Failed
}

public class VoteRecapService(IServiceScopeFactory scopeFactory, DiscordSocketClient discordClient)
{
    private const int DiscordMessageLimit = 1900;
    private static readonly TimeZoneInfo ParisTz =
        TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris");

    private ulong VoteAdminChannelId =>
        ulong.Parse(Environment.GetEnvironmentVariable("DISCORD_IDS_CHANNELS_VOTE_ADMIN") ?? "0");

    private ulong VoteCrewChannelId =>
        ulong.Parse(Environment.GetEnvironmentVariable("DISCORD_IDS_CHANNELS_VOTE_STAFF") ?? "0");

    private ulong RecapChannelId =>
        ulong.Parse(Environment.GetEnvironmentVariable("DISCORD_IDS_CHANNELS_VOTE_RECAP") ?? "0");

    public bool IsTrackedVoteChannel(ulong channelId) =>
        channelId == VoteAdminChannelId || channelId == VoteCrewChannelId;

    public async Task OnReadyAsync()
    {
        if (RecapChannelId == 0)
            return;

        foreach (var guild in discordClient.Guilds)
        {
            try
            {
                await RefreshRecapAsync(guild);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VoteRecapService] Refresh au démarrage ({guild.Name}) : {ex.Message}");
            }
        }
    }

    public async Task<VoteRecapRecordResult> TryRecordPollClosureAsync(
        SocketGuild guild,
        SocketGuildChannel pollChannel,
        IUserMessage message,
        Poll discordPoll,
        VotePoll poll)
    {
        if (!IsTrackedVoteChannel(poll.ChannelId))
            return VoteRecapRecordResult.Skipped;

        if (RecapChannelId == 0)
        {
            Console.WriteLine("[VoteRecapService] DISCORD_IDS_CHANNELS_VOTE_RECAP manquant.");
            return VoteRecapRecordResult.Failed;
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            if (await db.VoteParticipations.AnyAsync(p => p.PollMessageId == poll.MessageId))
                return VoteRecapRecordResult.Recorded;

            var kind = poll.ChannelId == VoteAdminChannelId ? "admin" : "crew";
            var closedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var voterIds = await GetVoterIdsAsync(message, discordPoll);
            var eligibleIds = GetEligibleUserIds(guild, pollChannel);

            foreach (var userId in eligibleIds)
            {
                db.VoteParticipations.Add(new VoteParticipation
                {
                    PollMessageId = poll.MessageId,
                    ChannelId = poll.ChannelId,
                    Kind = kind,
                    UserId = userId,
                    Voted = voterIds.Contains(userId),
                    ClosedAt = closedAt
                });
            }

            await EnsureRecapStateAsync(db, closedAt);
            await db.SaveChangesAsync();

            await RefreshRecapAsync(guild);
            return VoteRecapRecordResult.Recorded;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VoteRecapService] Enregistrement sondage {poll.MessageId} : {ex.Message}");
            SentrySdk.CaptureException(ex);
            return VoteRecapRecordResult.Failed;
        }
    }

    public async Task<bool> CanDeletePollAfterExpiryAsync(VotePoll poll)
    {
        if (!IsTrackedVoteChannel(poll.ChannelId))
            return true;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.VoteParticipations.AnyAsync(p => p.PollMessageId == poll.MessageId);
    }

    public async Task ResetPeriodAsync(SocketGuild guild)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var state = await EnsureRecapStateAsync(db, now);
        state.PeriodStartedAt = now;
        await db.SaveChangesAsync();
        await RefreshRecapAsync(guild);
    }

    private async Task<VoteRecapState> EnsureRecapStateAsync(AppDbContext db, long defaultPeriodStart)
    {
        var state = await db.VoteRecapStates.FirstOrDefaultAsync();
        if (state is not null)
            return state;

        state = new VoteRecapState
        {
            PeriodStartedAt = defaultPeriodStart,
            RecapMessageIdsCsv = string.Empty
        };
        db.VoteRecapStates.Add(state);
        return state;
    }

    private async Task RefreshRecapAsync(SocketGuild guild)
    {
        if (RecapChannelId == 0)
            return;

        if (guild.GetTextChannel(RecapChannelId) is not IMessageChannel recapChannel)
        {
            Console.WriteLine($"[VoteRecapService] Salon recap {RecapChannelId} introuvable.");
            return;
        }

        await guild.DownloadUsersAsync();

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var state = await db.VoteRecapStates.FirstOrDefaultAsync();
        if (state is null)
            return;

        var parts = BuildRecapMessages(guild, db, state.PeriodStartedAt);
        var existingIds = ParseMessageIds(state.RecapMessageIdsCsv);
        var newIds = await PublishPartsAsync(recapChannel, parts, existingIds);
        state.RecapMessageIdsCsv = string.Join(",", newIds);
        await db.SaveChangesAsync();
    }

    private List<string> BuildRecapMessages(SocketGuild guild, AppDbContext db, long periodStart)
    {
        var participations = db.VoteParticipations
            .Where(p => p.ClosedAt >= periodStart)
            .ToList();

        var adminChannelId = VoteAdminChannelId;
        var crewChannelId = VoteCrewChannelId;

        var adminPollCount = participations
            .Where(p => p.ChannelId == adminChannelId)
            .Select(p => p.PollMessageId)
            .Distinct()
            .Count();

        var crewPollCount = participations
            .Where(p => p.ChannelId == crewChannelId)
            .Select(p => p.PollMessageId)
            .Distinct()
            .Count();

        var periodEndLabel = FormatParisDate(DateTimeOffset.UtcNow);
        var periodStartLabel = FormatParisDate(DateTimeOffset.FromUnixTimeSeconds(periodStart));

        var header =
            "# 📊 Rapport de participation aux votes\n" +
            $"> 🗓️ **Période :** {periodStartLabel} → {periodEndLabel}\n" +
            $"> 🗳️ **Sondages :** {adminPollCount} sur #vote-admin · {crewPollCount} sur #vote-crew\n";

        var adminChannel = guild.GetChannel(adminChannelId) as SocketGuildChannel;
        var crewChannel = guild.GetChannel(crewChannelId) as SocketGuildChannel;

        var adminCohort = GetEligibleUserIds(guild, adminChannel).ToHashSet();
        var crewOnlyCohort = GetEligibleUserIds(guild, crewChannel)
            .Where(id => !adminCohort.Contains(id))
            .ToHashSet();

        var adminRows = BuildAdminRows(guild, participations, adminCohort, adminChannelId, crewChannelId);
        var crewRows = BuildCrewRows(guild, participations, crewOnlyCohort, crewChannelId);

        var adminBlock = FormatAdminTable(adminRows);
        var crewBlock = FormatCrewTable(crewRows);
        var legend =
            "```ansi\n" +
            " \u001b[1;32m● OK ≥75%\u001b[0m   \u001b[1;33m● À surveiller\u001b[0m   \u001b[1;31m● Inactif <50%\u001b[0m\n" +
            "```\n" +
            $"-# 🔄 Dernière mise à jour : {periodEndLabel} · Classement du moins actif au plus actif";

        var full = header + "\n## 🛡️ Admins\n" + adminBlock + "\n## 👥 Équipe\n" + crewBlock + "\n" + legend;
        return SplitForDiscord(full);
    }

    private List<ParticipationRow> BuildAdminRows(
        SocketGuild guild,
        List<VoteParticipation> participations,
        HashSet<ulong> adminCohort,
        ulong adminChannelId,
        ulong crewChannelId)
    {
        var rows = new List<ParticipationRow>();

        foreach (var userId in adminCohort)
        {
            var userParticipations = participations.Where(p => p.UserId == userId).ToList();
            if (userParticipations.Count == 0)
                continue;

            var adminStats = StatsForChannel(userParticipations, adminChannelId);
            var hasCrewAccess = guild.GetUser(userId)?.GetPermissions(guild.GetChannel(crewChannelId) as SocketGuildChannel)
                .ViewChannel ?? false;

            ParticipationStats? crewStats = null;
            if (hasCrewAccess)
                crewStats = StatsForChannel(userParticipations, crewChannelId);

            var votedTotal = adminStats.Voted + (crewStats?.Voted ?? 0);
            var eligibleTotal = adminStats.Eligible + (crewStats?.Eligible ?? 0);
            var percent = eligibleTotal == 0 ? 0 : (int)Math.Round(100.0 * votedTotal / eligibleTotal);

            rows.Add(new ParticipationRow(
                DisplayName(guild, userId),
                adminStats.Voted,
                adminStats.Eligible,
                crewStats?.Voted,
                crewStats?.Eligible,
                percent));
        }

        return rows.OrderBy(r => r.Percent).ThenBy(r => r.Name).ToList();
    }

    private static List<ParticipationRow> BuildCrewRows(
        SocketGuild guild,
        List<VoteParticipation> participations,
        HashSet<ulong> crewOnlyCohort,
        ulong crewChannelId)
    {
        var rows = new List<ParticipationRow>();

        foreach (var userId in crewOnlyCohort)
        {
            var userParticipations = participations.Where(p => p.UserId == userId).ToList();
            if (userParticipations.Count == 0)
                continue;

            var crewStats = StatsForChannel(userParticipations, crewChannelId);
            var percent = crewStats.Eligible == 0
                ? 0
                : (int)Math.Round(100.0 * crewStats.Voted / crewStats.Eligible);

            rows.Add(new ParticipationRow(
                DisplayName(guild, userId),
                null,
                null,
                crewStats.Voted,
                crewStats.Eligible,
                percent));
        }

        return rows.OrderBy(r => r.Percent).ThenBy(r => r.Name).ToList();
    }

    private static ParticipationStats StatsForChannel(List<VoteParticipation> userParticipations, ulong channelId)
    {
        var channelRows = userParticipations.Where(p => p.ChannelId == channelId).ToList();
        var polls = channelRows.Select(p => p.PollMessageId).Distinct().Count();
        var voted = channelRows.Where(p => p.Voted).Select(p => p.PollMessageId).Distinct().Count();
        return new ParticipationStats(voted, polls);
    }

    private static string FormatAdminTable(List<ParticipationRow> rows)
    {
        if (rows.Count == 0)
            return "_Aucune participation enregistrée sur la période._\n";

        var sb = new StringBuilder();
        sb.AppendLine("```ansi");
        sb.AppendLine(" \u001b[1;36m  PSEUDO        #vote-admin  #vote-crew  PARTICIPATION\u001b[0m");
        sb.AppendLine(" \u001b[0;30m────────────────────────────────────────────────────────────\u001b[0m");

        foreach (var row in rows)
        {
            var dot = DotColor(row.Percent);
            var name = PadRight(row.Name, 12);
            var adminCol = $"{row.AdminVoted}/{row.AdminEligible}".PadLeft(4) + "        ";
            var crewCol = row.CrewEligible is null
                ? "  ---      "
                : $"{row.CrewVoted}/{row.CrewEligible}".PadLeft(4) + "      ";
            var bar = ParticipationBar(row.Percent);
            sb.AppendLine($" {dot} \u001b[1;37m{name}\u001b[0m      {adminCol}{crewCol}  {bar}");
        }

        sb.Append("```");
        return sb.ToString();
    }

    private static string FormatCrewTable(List<ParticipationRow> rows)
    {
        if (rows.Count == 0)
            return "_Aucune participation enregistrée sur la période._\n";

        var sb = new StringBuilder();
        sb.AppendLine("```ansi");
        sb.AppendLine(" \u001b[1;36m  PSEUDO        #vote-crew  PARTICIPATION\u001b[0m");
        sb.AppendLine(" \u001b[0;30m─────────────────────────────────────────────\u001b[0m");

        foreach (var row in rows)
        {
            var dot = DotColor(row.Percent);
            var name = PadRight(row.Name, 12);
            var crewCol = $"{row.CrewVoted}/{row.CrewEligible}".PadLeft(4);
            var bar = ParticipationBar(row.Percent);
            sb.AppendLine($" {dot} \u001b[1;37m{name}\u001b[0m      {crewCol}       {bar}");
        }

        sb.Append("```");
        return sb.ToString();
    }

    private static string DotColor(int percent) => percent switch
    {
        >= 75 => "\u001b[1;32m●\u001b[0m",
        >= 50 => "\u001b[1;33m●\u001b[0m",
        _ => "\u001b[1;31m●\u001b[0m"
    };

    private static string ParticipationBar(int percent)
    {
        var filled = (int)Math.Round(percent / 10.0);
        filled = Math.Clamp(filled, 0, 10);
        var color = percent switch
        {
            >= 75 => "\u001b[1;32m",
            >= 50 => "\u001b[1;33m",
            _ => "\u001b[1;31m"
        };
        return $"{color}{new string('█', filled)}{new string('░', 10 - filled)}  {percent,3}%\u001b[0m";
    }

    private static string PadRight(string value, int width)
    {
        if (value.Length >= width)
            return value[..width];
        return value + new string(' ', width - value.Length);
    }

    private static string DisplayName(SocketGuild guild, ulong userId)
    {
        var user = guild.GetUser(userId);
        var name = user?.DisplayName ?? user?.Username ?? userId.ToString();
        if (name.Length > 12)
            return name[..12];
        return name;
    }

    private static HashSet<ulong> GetEligibleUserIds(SocketGuild guild, SocketGuildChannel? channel)
    {
        if (channel is null)
            return [];

        return guild.Users
            .Where(u => !u.IsBot && u.GetPermissions(channel).ViewChannel)
            .Select(u => u.Id)
            .ToHashSet();
    }

    private static async Task<HashSet<ulong>> GetVoterIdsAsync(IUserMessage message, Poll discordPoll)
    {
        var voterIds = new HashSet<ulong>();
        foreach (var answer in discordPoll.Answers)
        {
            var voters = await message
                .GetPollAnswerVotersAsync(answer.AnswerId)
                .FlattenAsync();

            foreach (var voter in voters)
                voterIds.Add(voter.Id);
        }

        return voterIds;
    }

    private static string FormatParisDate(DateTimeOffset utc)
    {
        var local = TimeZoneInfo.ConvertTime(utc, ParisTz);
        return local.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
    }

    private static List<ulong> ParseMessageIds(string csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? []
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => ulong.TryParse(s, out var id) ? id : 0)
                .Where(id => id != 0)
                .ToList();

    private static async Task<List<ulong>> PublishPartsAsync(
        IMessageChannel channel,
        List<string> parts,
        List<ulong> existingIds)
    {
        var result = new List<ulong>();

        for (var i = 0; i < parts.Count; i++)
        {
            var text = parts[i];
            if (i < existingIds.Count)
            {
                try
                {
                    if (await channel.GetMessageAsync(existingIds[i]) is IUserMessage existing)
                    {
                        await existing.ModifyAsync(m => m.Content = text);
                        result.Add(existing.Id);
                        continue;
                    }
                }
                catch
                {
                    // message supprimé
                }
            }

            var sent = await channel.SendMessageAsync(text);
            result.Add(sent.Id);
        }

        for (var j = parts.Count; j < existingIds.Count; j++)
        {
            try
            {
                if (await channel.GetMessageAsync(existingIds[j]) is IUserMessage extra)
                    await extra.DeleteAsync();
            }
            catch
            {
                // ignore
            }
        }

        return result;
    }

    private static List<string> SplitForDiscord(string text)
    {
        if (text.Length <= DiscordMessageLimit)
            return [text];

        var parts = new List<string>();
        var remaining = text;
        while (remaining.Length > DiscordMessageLimit)
        {
            var splitAt = remaining.LastIndexOf('\n', DiscordMessageLimit);
            if (splitAt < DiscordMessageLimit / 2)
                splitAt = DiscordMessageLimit;

            parts.Add(remaining[..splitAt].TrimEnd());
            remaining = remaining[splitAt..].TrimStart();
        }

        if (remaining.Length > 0)
            parts.Add(remaining);

        return parts;
    }

    private readonly record struct ParticipationStats(int Voted, int Eligible);

    private readonly record struct ParticipationRow(
        string Name,
        int? AdminVoted,
        int? AdminEligible,
        int? CrewVoted,
        int? CrewEligible,
        int Percent);
}
