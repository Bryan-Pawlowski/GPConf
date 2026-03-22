using GPConf.McpServer.DataAccess;
using GPConf.McpServer.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<GpConfDataAccess>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<SeasonTools>()
    .WithTools<RaceTools>();

await builder.Build().RunAsync();