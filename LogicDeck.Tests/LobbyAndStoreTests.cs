using LogicDeck.Core;
using Xunit;

namespace LogicDeck.Tests;

public class LobbyServiceTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(GateSpec.DisconnectGraceSeconds);

    [Fact]
    public void Duplicate_names_get_numeric_suffixes()
    {
        var lobby = new LobbyService();
        var a = lobby.Join("s1", "John");
        var b = lobby.Join("s2", "John");
        var c = lobby.Join("s3", "JOHN");

        Assert.Equal("John", a.Name);
        Assert.Equal("John 2", b.Name);
        Assert.Equal("JOHN 3", c.Name);
    }

    [Fact]
    public void Rejoining_same_session_keeps_identity()
    {
        var lobby = new LobbyService();
        var first = lobby.Join("s1", "Alice");
        var again = lobby.Join("s1", " Alice ");

        Assert.Equal(first.Name, again.Name);
        Assert.Equal(first.Color, again.Color);
    }

    [Fact]
    public void Renaming_frees_the_old_name()
    {
        var lobby = new LobbyService();
        lobby.Join("s1", "John");
        lobby.Join("s1", "Bob");
        var next = lobby.Join("s2", "john");

        Assert.Equal("Bob", lobby.Find("s1")!.Name);
        Assert.Equal("john", next.Name);
    }

    [Fact]
    public void Name_is_held_during_disconnect_grace_then_released()
    {
        var lobby = new LobbyService();
        lobby.Join("s1", "John");
        lobby.MarkDisconnected("s1");

        var duringGrace = lobby.Join("s2", "John");
        Assert.Equal("John 2", duringGrace.Name);

        var freed = lobby.Sweep(DateTime.UtcNow.Add(Grace).AddSeconds(5));
        Assert.Single(freed);

        var afterGrace = lobby.Join("s3", "John");
        Assert.Equal("John", afterGrace.Name);
    }

    [Fact]
    public void Blank_or_messy_names_are_cleaned()
    {
        var lobby = new LobbyService();
        var blank = lobby.Join("s1", "   ");
        Assert.Equal("Anonymous", blank.Name);

        var messy = lobby.Join("s2", "  Ada\t  Lovelace \u0007 ");
        Assert.Equal("Ada Lovelace", messy.Name);

        var longName = lobby.Join("s3", new string('x', 40));
        Assert.Equal(new string('x', GateSpec.MaxNameLength), longName.Name);
    }

    [Fact]
    public void Colors_prefer_least_used_palette_entry()
    {
        var lobby = new LobbyService();
        var first = lobby.Join("s1", "A");
        var second = lobby.Join("s2", "B");
        Assert.NotEqual(first.Color, second.Color);
    }
}

public class CircuitStoreTests
{
    private static MemberInfo Member(string id) => new(id, $"User {id}", "#ffffff");

    [Fact]
    public void Sweep_removes_only_idle_empty_circuits()
    {
        var store = new CircuitStore();
        var emptyIdle = store.Create("idle", 16);
        emptyIdle.LastActivityUtc = DateTime.UtcNow.AddMinutes(-30);

        var emptyFresh = store.Create("fresh", 16);
        emptyFresh.LastActivityUtc = DateTime.UtcNow;

        var occupiedIdle = store.Create("busy", 16);
        occupiedIdle.Members["s1"] = Member("s1");
        occupiedIdle.LastActivityUtc = DateTime.UtcNow.AddMinutes(-30);

        var removed = store.Sweep(DateTime.UtcNow, TimeSpan.FromMinutes(GateSpec.IdleCircuitMinutes));

        Assert.Equal([emptyIdle.Id], removed);
        Assert.NotNull(store.Find(emptyFresh.Id));
        Assert.NotNull(store.Find(occupiedIdle.Id));
    }

    [Fact]
    public void Summaries_reflect_members_and_activity_order()
    {
        var store = new CircuitStore();
        var older = store.Create("older", 24);
        Thread.Sleep(5);
        var newer = store.Create("newer", 8);
        newer.Members["s1"] = Member("s1");

        var summaries = store.Summaries();

        Assert.Equal(newer.Id, summaries[0].Id);
        Assert.Equal(1, summaries[0].NodeCount == 0 ? summaries[0].Members.Count : -1);
        Assert.Contains(summaries, s => s.GridSize == 24 && s.Title == "older");
    }

    [Fact]
    public void Removing_nodes_cascades_to_attached_wires()
    {
        var circuit = new Circuit { Id = "c1" };
        circuit.Nodes["in"] = new NodeItem { Id = "in", Type = GateType.Input };
        circuit.Nodes["and"] = new NodeItem { Id = "and", Type = GateType.And };
        circuit.Wires["w"] = new Wire { Id = "w", FromNodeId = "in", ToNodeId = "and", ToPin = 0 };

        var (nodesRemoved, wiresRemoved) =
            circuit.RemoveNodesAndAttachedWires(["in"]);

        Assert.Equal(["in"], nodesRemoved);
        Assert.Equal(["w"], wiresRemoved);
        Assert.Equal(["and"], circuit.Nodes.Keys.ToList());
        Assert.Empty(circuit.Wires);
    }
}
