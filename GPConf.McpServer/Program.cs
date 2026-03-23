using GPConf.McpServer.DataAccess;
using GPConf.McpServer.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = Microsoft.Extensions.Logging.LogLevel.Trace);

builder.Services.AddSingleton<GpConfDataAccess>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<SeasonTools>()
    .WithTools<RaceTools>()
    .WithTools<EntityTools>()
    .WithTools<QueryTools>();

await builder.Build().RunAsync();