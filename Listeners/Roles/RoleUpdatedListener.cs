using System.Net.Http.Json;
using Discord.WebSocket;
using TakeOverBot.DTOs;
using TakeOverBot.Interfaces;

namespace TakeOverBot.Listeners.Roles;

public class RoleUpdatedListener : IListener
{
    public void Register(DiscordSocketClient client)
    {
        client.RoleCreated += OnRoleCreated;
        client.RoleUpdated += OnRoleUpdated;
        client.RoleDeleted += OnRoleDeleted;
    }

    private static async Task OnRoleCreated(SocketRole role)
    {
        await UpdateWebRoles(role.Guild);
    }

    private static async Task OnRoleUpdated(SocketRole before, SocketRole after)
    {
        await UpdateWebRoles(after.Guild);
    }

    private static async Task OnRoleDeleted(SocketRole role)
    {
        await UpdateWebRoles(role.Guild);
    }

    private static async Task UpdateWebRoles(SocketGuild guild)
    {
        if (!bool.TryParse(Environment.GetEnvironmentVariable("WEBSITE_ENABLE_ROLE_UPDATE"), out var enableRoleUpdate) || !enableRoleUpdate)
            return;

        var baseUrl = Environment.GetEnvironmentVariable("WEBSITE_BASE_URL");
        var endpoint = Environment.GetEnvironmentVariable("WEBSITE_ROLE_UPDATE_ENDPOINT");
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(endpoint))
            return;

        var updatedRoles = guild.Roles.Select(
            role => new UpdatedRole { Id = role.Id.ToString(), Name = role.Name, Color = role.Colors.PrimaryColor.ToString() }
        ).ToList();

        var url = $"{baseUrl}{endpoint}";

        var handler = new HttpClientHandler();

        if (Environment.GetEnvironmentVariable("APP_ENVIRONMENT") == "DEV")
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

        var logChannelId = ulong.Parse(Environment.GetEnvironmentVariable("DISCORD_IDS_CHANNELS_LOGS") ?? "0");
        var logChannel = guild.GetChannel(logChannelId) as ISocketMessageChannel;

        HttpResponseMessage response;

        try
        {
            var client = new HttpClient(handler);
            response = await client.PostAsJsonAsync(url, updatedRoles);
        }
        catch (Exception e)
        {
            SentrySdk.CaptureException(e);

            if(logChannel is not null)
                await logChannel.SendMessageAsync($"❌ Échec de la synchronisation des rôles - {e.Message}");

            return;
        }

        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
        {
            if(logChannel is not null)
                await logChannel.SendMessageAsync("✅ Rôles synchronisés avec le site.");
        }
        else
        {
            var responseBody = await response.Content.ReadAsStringAsync();
            var exception = new HttpRequestException(
                $"Échec de la synchronisation des rôles. HTTP {(int)response.StatusCode} — Body: {responseBody}"
            );
            SentrySdk.CaptureException(exception);

            if(logChannel is not null)
                await logChannel.SendMessageAsync($"❌ Échec de la synchronisation des rôles. (HTTP {(int)response.StatusCode})");
        }
    }
}