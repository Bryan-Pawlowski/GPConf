using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using GPConf.DiscordBot.Commands;
using GPConf.DiscordBot.Services;
using Microsoft.Extensions.DependencyInjection;

var token = Environment.GetEnvironmentVariable("CONF_BOT_TOKEN")
    ?? throw new InvalidOperationException(
        "Set the CONF_BOT_TOKEN environment variable before running the bot.");

var client = new DiscordSocketClient(new DiscordSocketConfig
{
    GatewayIntents = GatewayIntents.None,
    AlwaysDownloadUsers = false,
});

var interactions = new InteractionService(client, new InteractionServiceConfig
{
    LogLevel = LogSeverity.Info,
    UseCompiledLambda = true,
});

var services = new ServiceCollection()
    .AddSingleton<DataService>()
    .AddSingleton(client)
    .AddSingleton(interactions)
    .BuildServiceProvider();

client.Log += log => { Console.WriteLine($"[Discord] {log.Message}"); return Task.CompletedTask; };
interactions.Log += log => { Console.WriteLine($"[Interactions] {log.Message}"); return Task.CompletedTask; };

client.Ready += async () =>
{
    // Register the DM-only read commands globally.
    await interactions.AddModulesAsync(typeof(ReadCommands).Assembly, services);
    await interactions.RegisterCommandsGloballyAsync();
    Console.WriteLine($"Bot ready as {client.CurrentUser.GlobalName} ({client.CurrentUser.Id})");
};

client.InteractionCreated += async interaction =>
{
    var ctx = new SocketInteractionContext(client, interaction);
    await interactions.ExecuteCommandAsync(ctx, services);
};

await client.LoginAsync(TokenType.Bot, token);
await client.StartAsync();

Console.WriteLine("Bot running. Press Ctrl+C to stop.");
await Task.Delay(-1);
