using LogicDeck.Core;
using LogicDeck.Web;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LogicDeck.Tests;

public class CircuitPersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "logicdeck-persist-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }
        catch
        {
        }
    }

    private static NodeItem Node(string id, GateType type, bool state = false) =>
        new() { Id = id, Type = type, State = state };

    [Fact]
    public void Circuits_survive_a_store_restart()
    {
        var store = new CircuitStore(new CircuitPersistence(_dir));
        var circuit = store.Create("Persisted bench", 24);
        circuit.Nodes["switch"] = Node("switch", GateType.Input, state: true);
        circuit.Nodes["andgate"] = Node("andgate", GateType.And);
        circuit.Nodes["lampout"] = Node("lampout", GateType.Output);
        circuit.Wires["wireone"] = new Wire { Id = "wireone", FromNodeId = "switch", ToNodeId = "andgate", ToPin = 0 };
        circuit.Wires["wiretwo"] = new Wire { Id = "wiretwo", FromNodeId = "andgate", ToNodeId = "lampout", ToPin = 0 };
        circuit.Touch();
        store.FlushDirty();

        // A fresh store over the same directory is exactly what a server restart does.
        var restored = new CircuitStore(new CircuitPersistence(_dir)).Find(circuit.Id);

        Assert.NotNull(restored);
        Assert.Equal("Persisted bench", restored!.Title);
        Assert.Equal(24, restored.GridSize);
        Assert.Equal(3, restored.Nodes.Count);
        Assert.Equal(2, restored.Wires.Count);
        Assert.True(restored.Nodes["switch"].State);
        Assert.Equal(GateType.And, restored.Nodes["andgate"].Type);
        Assert.False(restored.Running);
        Assert.Empty(restored.Members);
    }

    [Fact]
    public void Only_input_positions_are_persisted_as_runtime_state()
    {
        var store = new CircuitStore(new CircuitPersistence(_dir));
        var circuit = store.Create("Runtime only", 16);
        circuit.Nodes["clknode"] = Node("clknode", GateType.CLK, state: true);
        circuit.Nodes["flopone"] = Node("flopone", GateType.FlopD, state: true);
        circuit.Nodes["button1"] = Node("button1", GateType.Button, state: true);
        circuit.Touch();
        store.FlushDirty();

        var restored = new CircuitStore(new CircuitPersistence(_dir)).Find(circuit.Id)!;

        Assert.False(restored.Nodes["clknode"].State);
        Assert.False(restored.Nodes["flopone"].State);
        Assert.False(restored.Nodes["button1"].State);
    }

    [Fact]
    public void Deleting_a_circuit_removes_its_file()
    {
        var store = new CircuitStore(new CircuitPersistence(_dir));
        var circuit = store.Create("Doomed", 16);
        store.FlushDirty();
        var file = Path.Combine(_dir, circuit.Id + ".json");
        Assert.True(File.Exists(file));

        store.Delete(circuit.Id);

        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Sweeping_an_idle_room_removes_its_file()
    {
        var store = new CircuitStore(new CircuitPersistence(_dir));
        var circuit = store.Create("Idle", 16);
        circuit.LastActivityUtc = DateTime.UtcNow.AddMinutes(-30);
        store.FlushDirty();
        Assert.True(File.Exists(Path.Combine(_dir, circuit.Id + ".json")));

        store.Sweep(DateTime.UtcNow, TimeSpan.FromMinutes(GateSpec.IdleCircuitMinutes));

        Assert.False(File.Exists(Path.Combine(_dir, circuit.Id + ".json")));
    }

    [Fact]
    public void Unchanged_circuits_are_not_rewritten_on_every_flush()
    {
        var store = new CircuitStore(new CircuitPersistence(_dir));
        var circuit = store.Create("Steady", 16);
        store.FlushDirty();
        var file = Path.Combine(_dir, circuit.Id + ".json");
        var firstWrite = File.GetLastWriteTimeUtc(file);

        Thread.Sleep(20);
        store.FlushDirty(); // nothing touched → must be a no-op

        Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(file));
    }

    [Fact]
    public void A_corrupt_file_is_skipped_without_stopping_the_store()
    {
        var persistence = new CircuitPersistence(_dir);
        var store = new CircuitStore(persistence);
        var good = store.Create("Good", 16);
        store.FlushDirty();
        File.WriteAllText(Path.Combine(_dir, "corrupt.json"), "{ this is not valid json");

        var restored = new CircuitStore(new CircuitPersistence(_dir));

        Assert.Equal(1, restored.Count);
        Assert.NotNull(restored.Find(good.Id));
    }

    [Fact]
    public void A_file_with_dangling_wires_is_rejected()
    {
        var store = new CircuitStore(new CircuitPersistence(_dir));
        var circuit = store.Create("Bad wires", 16);
        store.FlushDirty();
        var file = Path.Combine(_dir, circuit.Id + ".json");
        var json = File.ReadAllText(file).Replace("\"wires\":[]", "\"wires\":[{\"id\":\"ghostwire\",\"fromNodeId\":\"nosuchnode\",\"toNodeId\":\"nosuchnode\",\"toPin\":0}]");
        File.WriteAllText(file, json);

        var restored = new CircuitStore(new CircuitPersistence(_dir));

        Assert.Null(restored.Find(circuit.Id));
    }
}

public class PersistenceRestartEndToEndTests
{
    [Fact]
    public async Task Restarting_the_server_restores_persisted_rooms()
    {
        var dir = Path.Combine(Path.GetTempPath(), "logicdeck-e2e-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var args = new[] { "--urls", "http://127.0.0.1:0", "--Persistence:Directory=" + dir };

        string roomId;
        string switchId;
        var first = Program.BuildApp(args, enablePersistence: true);
        await first.StartAsync();
        try
        {
            var url = first.Urls.First(u => u.StartsWith("http"));
            await using var conn = NewConnection(url);
            await conn.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await conn.InvokeAsync<SessionInfo>("JoinLobby", "survivor-sess", "Pia");
            var room = await conn.InvokeAsync<RoomSummary>("CreateRoom", "Survivor", 24);
            roomId = room.Id;
            await conn.InvokeAsync<CircuitSnapshot>("EnterRoom", room.Id);
            var input = await conn.InvokeAsync<NodeDto>("AddNode", GateType.Input, 32, 32);
            switchId = input.Id;
            var lamp = await conn.InvokeAsync<NodeDto>("AddNode", GateType.Output, 320, 32);
            await conn.InvokeAsync<WireDto>("AddWire", input.Id, lamp.Id, 0);
            await conn.InvokeAsync<bool>("ToggleInput", input.Id); // leave the switch ON
        }
        finally
        {
            await first.StopAsync().WaitAsync(TimeSpan.FromSeconds(10)); // final flush
            await first.DisposeAsync();
        }

        var second = Program.BuildApp(args, enablePersistence: true);
        await second.StartAsync();
        try
        {
            var url = second.Urls.First(u => u.StartsWith("http"));
            await using var conn = NewConnection(url);
            await conn.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await conn.InvokeAsync<SessionInfo>("JoinLobby", "survivor-sess", "Pia");

            var lobby = await conn.InvokeAsync<LobbyState>("FetchRooms");
            var restored = Assert.Single(lobby.Rooms, r => r.Id == roomId);
            Assert.Equal("Survivor", restored.Title);
            Assert.Equal(24, restored.GridSize);

            var snapshot = await conn.InvokeAsync<CircuitSnapshot>("EnterRoom", restored.Id);
            Assert.Equal(2, snapshot.Nodes.Count);
            Assert.Single(snapshot.Wires);
            Assert.True(snapshot.Nodes.Single(n => n.Id == switchId).State); // switch position survived
            Assert.False(snapshot.Running);
        }
        finally
        {
            await second.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await second.DisposeAsync();
            try
            {
                Directory.Delete(dir, true);
            }
            catch
            {
            }
        }
    }

    private static HubConnection NewConnection(string baseUrl) =>
        new HubConnectionBuilder()
            .WithUrl($"{baseUrl}/hubs/collab", options =>
            {
                options.Transports = HttpTransportType.WebSockets | HttpTransportType.ServerSentEvents | HttpTransportType.LongPolling;
            })
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            })
            .Build();
}
