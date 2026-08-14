using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using GPConf.DiscordBot.Commands;
using GPConf.DiscordBot.Services;
using GPConf.DiscordBot.Tools;

var token = Environment.GetEnvironmentVariable("CONF_BOT_TOKEN")
    ?? throw new InvalidOperationException(
        "Set the CONF_BOT_TOKEN environment variable before running the bot.");

// Local-only control channel for race-week automation skills (Claude Code / OpenCode) to make the
// live bot post to Discord — bound to 127.0.0.1 only, so no auth is needed. Stdio transport (used
// by GPConf.McpServer) spawns a new process per client and can't reach an already-running bot, so
// this needs HTTP transport instead, hosted in the same process as the Discord gateway connection.
var mcpPort = Environment.GetEnvironmentVariable("CONF_MCP_HTTP_PORT") ?? "5177";

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://127.0.0.1:{mcpPort}");

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

builder.Services
    .AddSingleton<DataService>()
    .AddSingleton<OllamaClient>()
    .AddSingleton<PickSessionStore>()
    .AddSingleton(client)
    .AddSingleton(interactions);

builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithTools<BotMcpTools>();

var app = builder.Build();
app.MapMcp();

client.Log += log => { Console.WriteLine($"[Discord] {log.Message}"); return Task.CompletedTask; };
interactions.Log += log => { Console.WriteLine($"[Interactions] {log.Message}"); return Task.CompletedTask; };

client.Ready += async () =>
{
    // Register the DM-only read commands globally.
    await interactions.AddModulesAsync(typeof(ReadCommands).Assembly, app.Services);
    await interactions.RegisterCommandsGloballyAsync();
    Console.WriteLine($"Bot ready as {client.CurrentUser.GlobalName} ({client.CurrentUser.Id})");
};

client.InteractionCreated += async interaction =>
{
    var ctx = new SocketInteractionContext(client, interaction);
    await interactions.ExecuteCommandAsync(ctx, app.Services);
};

await client.LoginAsync(TokenType.Bot, token);
await client.StartAsync();

Console.WriteLine($"Bot running, MCP HTTP transport on 127.0.0.1:{mcpPort}. Press Ctrl+C to stop.");
await app.RunAsync();
