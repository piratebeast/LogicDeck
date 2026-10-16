using LogicDeck.Core;
using LogicDeck.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LogicDeck.Tests;

public class EndToEndTests : IAsyncLifetime
{
    private WebApplication? _app;
    private readonly List<HubConnection> _conns = new();
    private string _baseUrl = "";

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var c in _conns)
            await c.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        if (_app != null) await _app.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        if (_app != null) await _app.DisposeAsync();
    }

    private async Task<HubConnection> StartServerAndConnect(int totalClients)
    {
        if (_app == null)
        {
            _app = Program.BuildApp(["--urls", "http://127.0.0.1:0"]);
            await _app.StartAsync();
            _baseUrl = _app.Urls.First(u => u.StartsWith("http"));
        }
        while (_conns.Count < totalClients)
        {
            var conn = new HubConnectionBuilder()
                .WithUrl($"{_baseUrl}/hubs/collab",
                    options => { options.Transports = HttpTransportType.WebSockets | HttpTransportType.ServerSentEvents | HttpTransportType.LongPolling; })
                .AddJsonProtocol(options =>
                {
                    options.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
                })
                .Build();
            await conn.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
            _conns.Add(conn);
        }
        return _conns[_conns.Count - 1];
    }

    [Fact]
    public async Task Same_name_joins_get_suffixes_and_rooms_share_live()
    {
        var a = await StartServerAndConnect(2);
        var b = _conns[0];

        var infoA = await a.InvokeAsync<SessionInfo>("JoinLobby", "sess-aaaa", "Dana");
        var infoB = await b.InvokeAsync<SessionInfo>("JoinLobby", "sess-bbbb", "dana");

        Assert.Equal("Dana", infoA.Name);
        Assert.Equal("dana 2", infoB.Name);
        Assert.NotEqual(infoA.Color, infoB.Color);

        var room = await a.InvokeAsync<RoomSummary>("CreateRoom", "E2E bench", 16);

        var nodeAdded = new TaskCompletionSource<NodeDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        b.On<NodeDto, string>("NodeAdded", (dto, _) => nodeAdded.TrySetResult(dto));

        await b.InvokeAsync<CircuitSnapshot>("EnterRoom", room.Id);
        await a.InvokeAsync<CircuitSnapshot>("EnterRoom", room.Id);
        await a.InvokeAsync<NodeDto>("AddNode", GateType.And, 100, 80);

        var dto = await nodeAdded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(GateType.And, dto.Type);

        var rooms = await b.InvokeAsync<LobbyState>("FetchRooms");
        var summary = rooms.Rooms.Single(r => r.Id == room.Id);
        Assert.Equal("E2E bench", summary.Title);
        Assert.Equal(2, summary.Members.Count);
    }

    [Fact]
    public async Task Simulation_propagates_signals_and_wire_rules_are_enforced()
    {
        var a = await StartServerAndConnect(2);
        var b = _conns[0];

        await a.InvokeAsync<SessionInfo>("JoinLobby", "sim-aaaa", "Alice");
        await b.InvokeAsync<SessionInfo>("JoinLobby", "sim-bbbb", "Bob");
        var room = await a.InvokeAsync<RoomSummary>("CreateRoom", "Sim bench", 8);
        await a.InvokeAsync<CircuitSnapshot>("EnterRoom", room.Id);
        await b.InvokeAsync<CircuitSnapshot>("EnterRoom", room.Id);

        var addedOnB = new List<string>();
        var twoAdded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        b.On<NodeDto, string>("NodeAdded", (dto, _) =>
        {
            lock (addedOnB)
            {
                addedOnB.Add(dto.Id);
                if (addedOnB.Count >= 2) twoAdded.TrySetResult();
            }
        });

        var sw = await a.InvokeAsync<NodeDto>("AddNode", GateType.Input, 0, 0);
        var lamp = await a.InvokeAsync<NodeDto>("AddNode", GateType.Output, 200, 0);

        await twoAdded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(lamp.Id, addedOnB);

        var noOutputPin = await Assert.ThrowsAnyAsync<HubException>(() =>
            a.InvokeAsync<WireDto>("AddWire", lamp.Id, sw.Id, 0));
        Assert.Contains("no output pin", noOutputPin.Message, StringComparison.OrdinalIgnoreCase);

        var andGate = await a.InvokeAsync<NodeDto>("AddNode", GateType.And, 400, 0);
        var norGate = await a.InvokeAsync<NodeDto>("AddNode", GateType.Nor, 600, 0);
        await a.InvokeAsync<WireDto>("AddWire", sw.Id, andGate.Id, 0);

        var takenInput = await Assert.ThrowsAnyAsync<HubException>(() =>
            a.InvokeAsync<WireDto>("AddWire", norGate.Id, andGate.Id, 0));
        Assert.Contains("already has a wire", takenInput.Message, StringComparison.OrdinalIgnoreCase);

        var simWire = await a.InvokeAsync<WireDto>("AddWire", sw.Id, lamp.Id, 0);

        var signals = new TaskCompletionSource<SignalFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        b.On<SignalFrame>("Signals", f =>
        {
            if (f.Wires.TryGetValue(simWire.Id, out var v) && v) signals.TrySetResult(f);
        });

        await a.InvokeAsync("SetRunning", true);
        var runningOnB = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        b.On<bool>("RunningChanged", v => runningOnB.TrySetResult(v));

        await a.InvokeAsync<bool>("ToggleInput", sw.Id);

        var frame = await signals.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(frame.Wires[simWire.Id]);
        Assert.True(frame.Outputs[lamp.Id]);
    }

    [Fact]
    public async Task Deleting_a_node_cascades_its_wires_to_other_clients()
    {
        var a = await StartServerAndConnect(2);
        var b = _conns[0];

        await a.InvokeAsync<SessionInfo>("JoinLobby", "cas-aaaa", "Cara");
        await b.InvokeAsync<SessionInfo>("JoinLobby", "cas-bbbb", "Chris");
        var room = await a.InvokeAsync<RoomSummary>("CreateRoom", "Cascade", 16);
        await a.InvokeAsync<CircuitSnapshot>("EnterRoom", room.Id);
        await b.InvokeAsync<CircuitSnapshot>("EnterRoom", room.Id);

        var n1 = await a.InvokeAsync<NodeDto>("AddNode", GateType.Input, 0, 0);
        var n2 = await a.InvokeAsync<NodeDto>("AddNode", GateType.Output, 300, 0);
        var wire = await a.InvokeAsync<WireDto>("AddWire", n1.Id, n2.Id, 0);

        var deleted = new TaskCompletionSource<(List<string>, List<string>)>(TaskCreationOptions.RunContinuationsAsynchronously);
        b.On<List<string>, List<string>, string>("NodesDeleted", (nodes, wires, _) =>
            deleted.TrySetResult((nodes, wires)));

        await a.InvokeAsync("DeleteNodes", new[] { n1.Id });

        var (nodesRemoved, wiresRemoved) = await deleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(n1.Id, nodesRemoved);
        var w = Assert.Single(wiresRemoved);
        Assert.Equal(wire.Id, w);
    }

    [Fact]
    public async Task Clock_ticks_while_running_and_drives_the_lamp()
    {
        var a = await StartServerAndConnect(1);

        await a.InvokeAsync<SessionInfo>("JoinLobby", "clk-aaaa", "Tess");
        var room = await a.InvokeAsync<RoomSummary>("CreateRoom", "Clock bench", 16);
        await a.InvokeAsync<CircuitSnapshot>("EnterRoom", room.Id);

        var clk = await a.InvokeAsync<NodeDto>("AddNode", GateType.CLK, 0, 0);
        var lamp = await a.InvokeAsync<NodeDto>("AddNode", GateType.Output, 200, 0);
        var wire = await a.InvokeAsync<WireDto>("AddWire", clk.Id, lamp.Id, 0);

        var phases = new List<bool>();
        var gotBoth = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        a.On<SignalFrame>("Signals", f =>
        {
            if (!f.Wires.TryGetValue(wire.Id, out var high)) return;
            if (!f.Sources.TryGetValue(clk.Id, out var phase)) return;
            lock (phases)
            {
                phases.Add(phase);
                if (phases.Distinct().Count() >= 2) gotBoth.TrySetResult();
            }
        });

        await a.InvokeAsync("SetRunning", true);

        await gotBoth.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(phases.Any());
    }

    [Fact]
    public async Task Duplicating_a_selection_copies_nodes_and_internal_wires_to_all_clients()
    {
        var a = await StartServerAndConnect(2);
        var b = _conns[0];

        await a.InvokeAsync<SessionInfo>("JoinLobby", "dup-aaaa", "Uma");
        await b.InvokeAsync<SessionInfo>("JoinLobby", "dup-bbbb", "Umar");
        var room = await a.InvokeAsync<RoomSummary>("CreateRoom", "Duplicate bench", 16);
        await a.InvokeAsync<CircuitSnapshot>("EnterRoom", room.Id);
        await b.InvokeAsync<CircuitSnapshot>("EnterRoom", room.Id);

        var addedNodes = new List<NodeDto>();
        var addedWires = new List<WireDto>();
        var recordClones = false;
        var originalsReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        b.On<NodeDto, string>("NodeAdded", (dto, _) =>
        {
            lock (addedNodes)
            {
                addedNodes.Add(dto);
                if (!recordClones && addedNodes.Count == 3 && addedWires.Count == 2)
                    originalsReceived.TrySetResult();
                if (recordClones && addedNodes.Count == 2 && addedWires.Count == 1)
                    received.TrySetResult();
            }
        });
        b.On<WireDto, string>("WireAdded", (dto, _) =>
        {
            lock (addedNodes)
            {
                addedWires.Add(dto);
                if (!recordClones && addedNodes.Count == 3 && addedWires.Count == 2)
                    originalsReceived.TrySetResult();
                if (recordClones && addedNodes.Count == 2 && addedWires.Count == 1)
                    received.TrySetResult();
            }
        });

        var input = await a.InvokeAsync<NodeDto>("AddNode", GateType.Input, 0, 0);
        var gate = await a.InvokeAsync<NodeDto>("AddNode", GateType.And, 160, 0);
        var lamp = await a.InvokeAsync<NodeDto>("AddNode", GateType.Output, 320, 0);
        await a.InvokeAsync<WireDto>("AddWire", input.Id, gate.Id, 0);
        await a.InvokeAsync<WireDto>("AddWire", gate.Id, lamp.Id, 0);
        await originalsReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        lock (addedNodes)
        {
            addedNodes.Clear();
            addedWires.Clear();
        }

        recordClones = true;
        var result = await a.InvokeAsync<DuplicationDto>("DuplicateNodes", new[] { input.Id, gate.Id });
        await received.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, result.Nodes.Count);
        var inputCopy = result.Nodes.Single(n => n.Type == GateType.Input);
        var gateCopy = result.Nodes.Single(n => n.Type == GateType.And);
        Assert.Equal(input.X + 32, inputCopy.X);
        Assert.Equal(input.Y + 32, inputCopy.Y);
        Assert.Equal(gate.X + 32, gateCopy.X);
        Assert.Equal(gate.Y + 32, gateCopy.Y);
        Assert.NotEqual(input.Id, inputCopy.Id);
        Assert.NotEqual(gate.Id, gateCopy.Id);

        var copiedWire = Assert.Single(result.Wires);
        Assert.Equal(inputCopy.Id, copiedWire.FromNodeId);
        Assert.Equal(gateCopy.Id, copiedWire.ToNodeId);
        Assert.Equal(0, copiedWire.ToPin);

        lock (addedNodes)
        {
            Assert.Equal(2, addedNodes.Count);
            var wire = Assert.Single(addedWires);
            Assert.Equal(inputCopy.Id, wire.FromNodeId);
            Assert.Equal(gateCopy.Id, wire.ToNodeId);
        }
    }

    [Fact]
    public async Task Push_button_sets_a_high_signal_while_held()
    {
        var a = await StartServerAndConnect(1);
        await a.InvokeAsync<SessionInfo>("JoinLobby", "btn-aaaa", "Bea");
        var room = await a.InvokeAsync<RoomSummary>("CreateRoom", "Button bench", 16);
        await a.InvokeAsync<CircuitSnapshot>("EnterRoom", room.Id);

        var button = await a.InvokeAsync<NodeDto>("AddNode", GateType.Button, 0, 0);
        var lamp = await a.InvokeAsync<NodeDto>("AddNode", GateType.Output, 200, 0);
        await a.InvokeAsync<WireDto>("AddWire", button.Id, lamp.Id, 0);
        var high = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        a.On<SignalFrame>("Signals", frame =>
        {
            if (frame.Outputs.GetValueOrDefault(lamp.Id)) high.TrySetResult();
        });

        await a.InvokeAsync("SetRunning", true);
        var pressed = await a.InvokeAsync<bool>("SetButton", button.Id, true);

        await high.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(pressed);
    }

    [Fact]
    public async Task Packaging_gates_creates_a_reusable_ic_that_drives_its_output()
    {
        var a = await StartServerAndConnect(1);
        await a.InvokeAsync<SessionInfo>("JoinLobby", "ic-aaaa", "Ivy");
        var room = await a.InvokeAsync<RoomSummary>("CreateRoom", "IC bench", 16);
        await a.InvokeAsync<CircuitSnapshot>("EnterRoom", room.Id);

        var gate = await a.InvokeAsync<NodeDto>("AddNode", GateType.And, 0, 0);
        var labels = new Dictionary<string, string>
        {
            [$"{gate.Id}:in0"] = "A",
            [$"{gate.Id}:in1"] = "B"
        };
        var outputLabels = new Dictionary<string, string> { [gate.Id] = "Y" };
        var packaged = await a.InvokeAsync<PackageIcResult>("PackageIc", "AND2", new[] { gate.Id }, labels, outputLabels);
        Assert.Equal(2, packaged.Definition.Inputs.Count);
        Assert.Equal("A", packaged.Definition.Inputs[0].Label);
        Assert.Equal("B", packaged.Definition.Inputs[1].Label);
        Assert.Equal("Y", packaged.Definition.Outputs[0].Label);

        var ic = await a.InvokeAsync<NodeDto>("AddIcNode", GateType.Ic, 220, 0, packaged.Definition.Id);
        var aInput = await a.InvokeAsync<NodeDto>("AddNode", GateType.Input, 0, 160);
        var bInput = await a.InvokeAsync<NodeDto>("AddNode", GateType.Input, 0, 240);
        var lamp = await a.InvokeAsync<NodeDto>("AddNode", GateType.Output, 440, 0);
        await a.InvokeAsync<WireDto>("AddWire", aInput.Id, ic.Id, 0);
        await a.InvokeAsync<WireDto>("AddWire", bInput.Id, ic.Id, 1);
        await a.InvokeAsync<WireDto>("AddIcWire", ic.Id, lamp.Id, 0, 0);
        var high = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        a.On<SignalFrame>("Signals", frame =>
        {
            if (frame.Outputs.GetValueOrDefault(lamp.Id)) high.TrySetResult();
        });

        await a.InvokeAsync("SetRunning", true);
        await a.InvokeAsync<bool>("ToggleInput", aInput.Id);
        await a.InvokeAsync<bool>("ToggleInput", bInput.Id);

        await high.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(GateType.Ic, ic.Type);
    }

    [Fact]
    public async Task Seven_segment_display_decodes_four_switches_into_a_digit()
    {
        var a = await StartServerAndConnect(1);

        await a.InvokeAsync<SessionInfo>("JoinLobby", "seg-aaaa", "Sena");
        var room = await a.InvokeAsync<RoomSummary>("CreateRoom", "Seg bench", 16);
        await a.InvokeAsync<CircuitSnapshot>("EnterRoom", room.Id);

        var seg = await a.InvokeAsync<NodeDto>("AddNode", GateType.Seg7, 400, 0);
        var switches = new List<NodeDto>();
        for (var bit = 0; bit < 4; bit++)
        {
            var sw = await a.InvokeAsync<NodeDto>("AddNode", GateType.Input, 0, bit * 80);
            await a.InvokeAsync<WireDto>("AddWire", sw.Id, seg.Id, bit);
            switches.Add(sw);
        }

        var showedFive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        a.On<SignalFrame>("Signals", frame =>
        {
            if (frame.Displays.TryGetValue(seg.Id, out var mask) && mask == 0x6D)
                showedFive.TrySetResult();
        });

        await a.InvokeAsync("SetRunning", true);
        await a.InvokeAsync<bool>("ToggleInput", switches[0].Id); // +1
        await a.InvokeAsync<bool>("ToggleInput", switches[2].Id); // +4  → nibble 5

        await showedFive.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task D_flip_flop_latches_a_level_on_each_button_pulse()
    {
        var a = await StartServerAndConnect(1);

        await a.InvokeAsync<SessionInfo>("JoinLobby", "ff-aaaa", "Faye");
        var room = await a.InvokeAsync<RoomSummary>("CreateRoom", "Flop bench", 16);
        await a.InvokeAsync<CircuitSnapshot>("EnterRoom", room.Id);

        var flop = await a.InvokeAsync<NodeDto>("AddNode", GateType.FlopD, 200, 0);
        var dSwitch = await a.InvokeAsync<NodeDto>("AddNode", GateType.Input, 0, 0);
        var pulse = await a.InvokeAsync<NodeDto>("AddNode", GateType.Button, 0, 120);
        var lamp = await a.InvokeAsync<NodeDto>("AddNode", GateType.Output, 420, 0);
        await a.InvokeAsync<WireDto>("AddWire", dSwitch.Id, flop.Id, 0);
        await a.InvokeAsync<WireDto>("AddWire", pulse.Id, flop.Id, 1);
        await a.InvokeAsync<WireDto>("AddIcWire", flop.Id, lamp.Id, 0, 0); // Q (out0) drives the lamp

        var latchedHigh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var latchedLow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var highDone = false;
        a.On<SignalFrame>("Signals", frame =>
        {
            if (!frame.Flops.TryGetValue(flop.Id, out var q)) return;
            if (q && !highDone) { highDone = true; latchedHigh.TrySetResult(); }
            else if (!q && highDone && !frame.Outputs.GetValueOrDefault(lamp.Id, true))
                latchedLow.TrySetResult();
        });

        await a.InvokeAsync("SetRunning", true);
        await a.InvokeAsync<bool>("ToggleInput", dSwitch.Id); // D = 1
        await a.InvokeAsync<bool>("SetButton", pulse.Id, true); // rising edge latches 1
        await latchedHigh.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await a.InvokeAsync<bool>("SetButton", pulse.Id, false);
        await a.InvokeAsync<bool>("ToggleInput", dSwitch.Id); // D changes while clock low
        await a.InvokeAsync<bool>("SetButton", pulse.Id, true); // next rising edge latches 0
        await latchedLow.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Only_the_owner_can_delete_a_circuit()
    {
        var a = await StartServerAndConnect(2);
        var b = _conns[0];
        await a.InvokeAsync<SessionInfo>("JoinLobby", "own-aaaa", "Ola");
        await b.InvokeAsync<SessionInfo>("JoinLobby", "own-bbbb", "Omar");
        var room = await a.InvokeAsync<RoomSummary>("CreateRoom", "Owned circuit", 16);
        await a.InvokeAsync<CircuitSnapshot>("EnterRoom", room.Id);
        await b.InvokeAsync<CircuitSnapshot>("EnterRoom", room.Id);

        var denied = await Assert.ThrowsAnyAsync<HubException>(() => b.InvokeAsync("DeleteRoom", room.Id));
        Assert.Contains("owner", denied.Message, StringComparison.OrdinalIgnoreCase);
        await a.InvokeAsync("DeleteRoom", room.Id);

        var rooms = await a.InvokeAsync<LobbyState>("FetchRooms");
        Assert.DoesNotContain(rooms.Rooms, r => r.Id == room.Id);
    }

    [Fact]
    public async Task Importing_a_circuit_file_replaces_the_room_snapshot()
    {
        var a = await StartServerAndConnect(1);
        await a.InvokeAsync<SessionInfo>("JoinLobby", "imp-aaaa", "Ivy");
        var room = await a.InvokeAsync<RoomSummary>("CreateRoom", "Before import", 16);
        await a.InvokeAsync<CircuitSnapshot>("EnterRoom", room.Id);
        var input = await a.InvokeAsync<NodeDto>("AddNode", GateType.Input, 0, 0);
        var gate = await a.InvokeAsync<NodeDto>("AddNode", GateType.Xnor, 200, 0);
        var wire = await a.InvokeAsync<WireDto>("AddWire", input.Id, gate.Id, 0);

        var snapshot = await a.InvokeAsync<CircuitSnapshot>("ImportCircuit", new CircuitImportDto(
            1,
            "Imported circuit",
            8,
            new[] { input with { X = 32, Y = 48 }, gate with { X = 256, Y = 48 } },
            new[] { wire },
            []));

        Assert.Equal("Imported circuit", snapshot.Title);
        Assert.Equal(8, snapshot.GridSize);
        Assert.Equal(2, snapshot.Nodes.Count);
        Assert.Single(snapshot.Wires);
    }

    [Fact]
    public async Task Truth_table_reports_every_input_combination()
    {
        var a = await StartServerAndConnect(1);
        await a.InvokeAsync<SessionInfo>("JoinLobby", "tt-aaaa", "Tara");
        var room = await a.InvokeAsync<RoomSummary>("CreateRoom", "Truth bench", 16);
        await a.InvokeAsync<CircuitSnapshot>("EnterRoom", room.Id);

        var inA = await a.InvokeAsync<NodeDto>("AddNode", GateType.Input, 0, 0);
        var inB = await a.InvokeAsync<NodeDto>("AddNode", GateType.Input, 0, 96);
        var andGate = await a.InvokeAsync<NodeDto>("AddNode", GateType.And, 200, 48);
        var lamp = await a.InvokeAsync<NodeDto>("AddNode", GateType.Output, 400, 48);
        await a.InvokeAsync<WireDto>("AddWire", inA.Id, andGate.Id, 0);
        await a.InvokeAsync<WireDto>("AddWire", inB.Id, andGate.Id, 1);
        await a.InvokeAsync<WireDto>("AddWire", andGate.Id, lamp.Id, 0);

        var table = await a.InvokeAsync<TruthTableDto>("ComputeTruthTable");

        Assert.Equal(2, table.Inputs.Count);
        Assert.Equal(inA.Id, table.Inputs[0].Id);
        Assert.Equal(inB.Id, table.Inputs[1].Id);
        Assert.Equal(lamp.Id, Assert.Single(table.Outputs).Id);
        Assert.Equal(4, table.Rows.Count);
        Assert.False(Assert.Single(table.Rows[0].Outputs));
        Assert.True(table.Rows[3].Inputs[0]);
        Assert.True(table.Rows[3].Inputs[1]);
        Assert.True(Assert.Single(table.Rows[3].Outputs));
    }

    [Fact]
    public async Task Truth_table_requires_at_least_one_input_switch()
    {
        var a = await StartServerAndConnect(1);
        await a.InvokeAsync<SessionInfo>("JoinLobby", "tt2-aaaa", "Tess");
        var room = await a.InvokeAsync<RoomSummary>("CreateRoom", "Empty bench", 16);
        await a.InvokeAsync<CircuitSnapshot>("EnterRoom", room.Id);

        var error = await Assert.ThrowsAnyAsync<HubException>(() => a.InvokeAsync<TruthTableDto>("ComputeTruthTable"));
        Assert.Contains("input switch", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Tidy_layout_arranges_connected_nodes_into_columns()
    {
        var a = await StartServerAndConnect(1);
        await a.InvokeAsync<SessionInfo>("JoinLobby", "tidy-aaaa", "Tia");
        var room = await a.InvokeAsync<RoomSummary>("CreateRoom", "Tidy bench", 16);
        await a.InvokeAsync<CircuitSnapshot>("EnterRoom", room.Id);
        var input = await a.InvokeAsync<NodeDto>("AddNode", GateType.Input, 400, 400);
        var gate = await a.InvokeAsync<NodeDto>("AddNode", GateType.And, 100, 100);
        var output = await a.InvokeAsync<NodeDto>("AddNode", GateType.Output, 0, 0);
        await a.InvokeAsync<WireDto>("AddWire", input.Id, gate.Id, 0);
        await a.InvokeAsync<WireDto>("AddWire", gate.Id, output.Id, 0);

        var result = await a.InvokeAsync<TidyLayoutDto>("TidyLayout", Array.Empty<string>());
        var positions = result.Nodes.ToDictionary(n => n.Id, n => n.X);
        Assert.True(positions[input.Id] < positions[gate.Id]);
        Assert.True(positions[gate.Id] < positions[output.Id]);
    }

    [Fact]
    public async Task Undo_and_redo_revert_the_last_edit_for_every_client()
    {
        var a = await StartServerAndConnect(2);
        var b = _conns[0];
        await a.InvokeAsync<SessionInfo>("JoinLobby", "undo-aaaa", "Ula");
        await b.InvokeAsync<SessionInfo>("JoinLobby", "undo-bbbb", "Umar");
        var room = await a.InvokeAsync<RoomSummary>("CreateRoom", "Undo bench", 16);
        await a.InvokeAsync<CircuitSnapshot>("EnterRoom", room.Id);
        await b.InvokeAsync<CircuitSnapshot>("EnterRoom", room.Id);

        var historyOnB = new TaskCompletionSource<(bool CanUndo, bool CanRedo)>(TaskCreationOptions.RunContinuationsAsynchronously);
        b.On<bool, bool>("HistoryChanged", (canUndo, canRedo) => historyOnB.TrySetResult((canUndo, canRedo)));
        var replacedOnB = new TaskCompletionSource<CircuitSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        b.On<CircuitSnapshot>("CircuitReplaced", s => replacedOnB.TrySetResult(s));

        var node = await a.InvokeAsync<NodeDto>("AddNode", GateType.And, 100, 80);
        var (canUndo, canRedo) = await historyOnB.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(canUndo);
        Assert.False(canRedo);

        var undone = await a.InvokeAsync<CircuitSnapshot>("Undo");
        Assert.DoesNotContain(undone.Nodes, n => n.Id == node.Id);

        var seenByB = await replacedOnB.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.DoesNotContain(seenByB.Nodes, n => n.Id == node.Id);

        var redone = await a.InvokeAsync<CircuitSnapshot>("Redo");
        Assert.Contains(redone.Nodes, n => n.Id == node.Id);
    }

    [Fact]
    public async Task Undo_restores_a_deleted_component()
    {
        var a = await StartServerAndConnect(1);
        await a.InvokeAsync<SessionInfo>("JoinLobby", "undel-aaa", "Una");
        var room = await a.InvokeAsync<RoomSummary>("CreateRoom", "Undo delete", 16);
        await a.InvokeAsync<CircuitSnapshot>("EnterRoom", room.Id);

        var node = await a.InvokeAsync<NodeDto>("AddNode", GateType.Xor, 0, 0);
        await a.InvokeAsync("DeleteNodes", new[] { node.Id });

        var restored = await a.InvokeAsync<CircuitSnapshot>("Undo");

        Assert.Contains(restored.Nodes, n => n.Id == node.Id);
    }

    [Fact]
    public async Task Undo_without_history_is_rejected()
    {
        var a = await StartServerAndConnect(1);
        await a.InvokeAsync<SessionInfo>("JoinLobby", "undnon-aaa", "Uri");
        var room = await a.InvokeAsync<RoomSummary>("CreateRoom", "No history", 16);
        await a.InvokeAsync<CircuitSnapshot>("EnterRoom", room.Id);

        var error = await Assert.ThrowsAnyAsync<HubException>(() => a.InvokeAsync<CircuitSnapshot>("Undo"));
        Assert.Contains("undo", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
