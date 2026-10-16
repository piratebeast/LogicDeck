using System.Text.Json.Serialization;
using LogicDeck.Core;
using LogicDeck.Web;
using LogicDeck.Web.Hubs;

namespace LogicDeck.Web;

public static class Program
{
    private const string DefaultPersistenceDirectory = "App_Data/circuits";
    private const int DefaultPersistenceIntervalSeconds = 2;

    public static void Main(string[] args) => BuildApp(args, enablePersistence: true).Run();

    public static WebApplication BuildApp(string[] args, bool enablePersistence = false)
    {
        var builder = WebApplication.CreateBuilder(args);

        CircuitPersistence? persistence = null;
        var flushInterval = TimeSpan.FromSeconds(DefaultPersistenceIntervalSeconds);
        if (enablePersistence)
        {
            var configured = builder.Configuration["Persistence:Directory"];
            var directory = string.IsNullOrWhiteSpace(configured) ? DefaultPersistenceDirectory : configured;
            if (!Path.IsPathRooted(directory))
                directory = Path.Combine(builder.Environment.ContentRootPath, directory);
            persistence = new CircuitPersistence(directory);
            if (int.TryParse(builder.Configuration["Persistence:IntervalSeconds"], out var seconds) && seconds > 0)
                flushInterval = TimeSpan.FromSeconds(seconds);
        }

        builder.Services.AddSingleton<LobbyService>();
        builder.Services.AddSingleton(_ => new CircuitStore(persistence));
        builder.Services.AddHostedService<Sweeper>();
        builder.Services.AddHostedService<ClockTicker>();
        if (persistence is not null)
        {
            builder.Services.AddSingleton<IHostedService>(sp => new PersistenceService(
                sp.GetRequiredService<CircuitStore>(),
                sp.GetRequiredService<ILogger<PersistenceService>>(),
                flushInterval));
        }
        builder.Services.AddSignalR(options => { options.EnableDetailedErrors = true; })
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            });

        var app = builder.Build();

        app.UseDefaultFiles();
        app.UseStaticFiles();

        app.MapHub<CollabHub>("/hubs/collab");

        app.MapGet("/circuit/{id}", (HttpContext ctx) =>
        {
            var file = ctx.RequestServices.GetRequiredService<IWebHostEnvironment>()
                .WebRootFileProvider.GetFileInfo("circuit.html");
            return Results.File(file.CreateReadStream(), "text/html");
        });

        return app;
    }
}
