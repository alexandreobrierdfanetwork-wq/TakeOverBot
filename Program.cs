using Discord;
using Discord.WebSocket;
using DotNetEnv;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TakeOverBot;
using TakeOverBot.Handler;
using TakeOverBot.Services;

Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });

Env.Load(".env");
Env.Load(".env.local");

var databasePath = Environment.GetEnvironmentVariable("APP_DATABASE_PATH") ?? "Data/app.db";
var token = Environment.GetEnvironmentVariable("DISCORD_TOKEN");
var isSentryEnabled = bool.Parse(Environment.GetEnvironmentVariable("SENTRY_ENABLE") ?? "false");

if (isSentryEnabled)
{
    SentrySdk.Init(options =>
    {
        options.Dsn = Environment.GetEnvironmentVariable("SENTRY_DSN");
        options.ServerName = Environment.GetEnvironmentVariable("APP_SERVER_NAME");
        options.Debug = Environment.GetEnvironmentVariable("APP_ENVIRONMENT") == "true";
        options.Release = Environment.GetEnvironmentVariable("APP_VERSION");
        options.Environment = Environment.GetEnvironmentVariable("APP_ENVIRONMENT");
        options.TracesSampleRate = 0.01; // 1% of transactions
    });
}

if (string.IsNullOrWhiteSpace(token))
{
    throw new InvalidOperationException("La variable d'environnement DISCORD_TOKEN est manquante.");
}

var dataDirectory = Path.GetDirectoryName(databasePath);

if (!string.IsNullOrWhiteSpace(dataDirectory))
{
    Directory.CreateDirectory(dataDirectory);
}

var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
    .UseSqlite($"Data Source={databasePath}")
    .Options;

await using (var dbContext = new AppDbContext(dbOptions))
{
    await dbContext.Database.MigrateAsync();
}

var config = new DiscordSocketConfig
{
    GatewayIntents = GatewayIntents.AllUnprivileged
                     | GatewayIntents.MessageContent
                     | GatewayIntents.GuildMembers
};

var client = new DiscordSocketClient(config);

var services = new ServiceCollection()
    .AddHttpClient()
    .AddSingleton(client)
    .AddSingleton<FacebookService>()
    .AddSingleton<VoteRecapService>()
    .AddSingleton<VoteService>()
    .AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={databasePath}"))
    .BuildServiceProvider();

var facebookService = services.GetRequiredService<FacebookService>();
var voteService = services.GetRequiredService<VoteService>();

_ = Task.Run(async () =>
{
    try
    {
        await facebookService.FetchLastPost();

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        while (await timer.WaitForNextTickAsync())
        {
            await facebookService.FetchLastPost();
        }
    }
    catch (Exception e)
    {
        Console.WriteLine($"[FacebookService] Erreur : {e.Message}");
    }
});

_ = Task.Run(async () =>
{
    try { await voteService.StartAsync(); }
    catch (Exception e) { Console.WriteLine($"[VotePollService] Erreur : {e.Message}"); }
});

var handler = new CommandHandler(client, services);
_ = new ListenerHandler(client);

client.Log += msg =>
{
    Console.WriteLine(msg.ToString());
    return Task.CompletedTask;
};

client.Ready += async () =>
{
    await handler.RegisterCommandsAsync();
    Console.WriteLine("Commandes et listeners enregistrés !");
    _ = Task.Run(async () =>
    {
        try { await voteService.OnReadyAsync(); }
        catch (Exception e) { Console.WriteLine($"[VoteService] OnReady : {e.Message}"); }
    });
};

client.SlashCommandExecuted += handler.HandleInteractionAsync;

await client.LoginAsync(TokenType.Bot, token);
await client.StartAsync();
await Task.Delay(-1);