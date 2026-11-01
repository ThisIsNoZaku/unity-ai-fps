using GoalDrivenAI;
using GoalDrivenAI.Commands;
using GoalDrivenAI.Stealth;

namespace GoalDrivenAI.Tests;

public sealed class MazeScenarioTests
{
    private sealed class Clock : IClock { public double Now { get; set; } }

    [TestCase(0)] [TestCase(42)] [TestCase(1234)]
    public void ObjectivesAreDistinctReachableOrderedAndGateTheExit(int seed)
    {
        var map = new MazeMap(seed);
        var scenario = new MazeScenario(new Clock(), map);
        Assert.That(scenario.Checkpoints.Count, Is.EqualTo(3));
        Assert.That(scenario.Checkpoints.Distinct().Count(), Is.EqualTo(3));
        Assert.That(scenario.CanEscape(MazeScenario.InfiltratorId, scenario.Exit), Is.False);
        Assert.That(scenario.Visit(MazeScenario.InfiltratorId, scenario.Checkpoints[1]), Is.False);
        Assert.That(scenario.Visit(MazeScenario.CommanderId, scenario.Checkpoints[0]), Is.False);
        Assert.That(scenario.Visit("observer", scenario.Checkpoints[0]), Is.False);
        for (int i = 0; i < scenario.Checkpoints.Count; i++)
        {
            var p = scenario.Checkpoints[i];
            var cell = ((int)(p.X / MazeScenario.CellSize), (int)(p.Z / MazeScenario.CellSize));
            Assert.That(map.Walkable(cell.Item1, cell.Item2), Is.True);
            Assert.That(map.Route((1, 1), cell).Last(), Is.EqualTo(cell));
            Assert.That(scenario.Visit(MazeScenario.InfiltratorId, new Position(p.X, p.Y + 3, p.Z)), Is.False);
            Assert.That(scenario.Visit(MazeScenario.InfiltratorId, p), Is.True);
            Assert.That(scenario.Visit(MazeScenario.InfiltratorId, p), Is.False);
            Assert.That(scenario.Reached, Is.EqualTo(i + 1));
        }
        Assert.That(scenario.CanEscape(MazeScenario.CommanderId, scenario.Exit), Is.False);
        Assert.That(scenario.CanEscape(MazeScenario.InfiltratorId, scenario.Checkpoints[0]), Is.False);
        Assert.That(scenario.CanEscape(MazeScenario.InfiltratorId, scenario.Exit), Is.True);
    }

    [Test]
    public void CommanderHasFactionScopedPersistentOrdersAndCannotCommandTheInfiltrator()
    {
        var scenario = new MazeScenario(new Clock(), new MazeMap());
        Assert.That(MazeScenario.FactionOf(MazeScenario.InfiltratorId), Is.EqualTo("infiltrator"));
        Assert.That(MazeScenario.FactionOf(MazeScenario.CommanderId), Is.EqualTo("guards"));
        foreach (string guard in scenario.GuardIds)
        {
            Assert.That(MazeScenario.FactionOf(guard), Is.EqualTo("guards"));
            var order = scenario.Commands.GetPrimary(guard)!;
            Assert.That(order.Issuer, Is.EqualTo(MazeScenario.CommanderId));
            Assert.That(order.Status, Is.EqualTo(OrderStatus.Active));
            Assert.That(order.Request.Constraints.Completion, Is.EqualTo(CompletionMode.Persistent));
        }
        Assert.That(scenario.Protect(MazeScenario.InfiltratorId, 0, 1), Is.False);
        Assert.That(scenario.Protect("observer", 0, 1), Is.False);
        Assert.That(scenario.Protect(scenario.GuardIds[0], 1, 1), Is.False);
        Assert.That(scenario.Commands.Submit(MazeScenario.CommanderId,
            new OrderRequest("hostile", MazeScenario.InfiltratorId, OrderType.HoldArea, scenario.Checkpoints[0])).Accepted, Is.False);
        Assert.That(scenario.Protect(MazeScenario.CommanderId, 0, 2), Is.True);
        Assert.That(scenario.DefenseFor(0), Is.EqualTo(2));
        Assert.That(scenario.Protect(MazeScenario.CommanderId, -1, 2), Is.False);
        Assert.That(scenario.Protect(MazeScenario.CommanderId, 0, 99), Is.False);
    }

    [Test]
    public void SensoryEmergencySuspendsDefenseAndReplacementResumesWithoutLosingAssignment()
    {
        var scenario = new MazeScenario(new Clock(), new MazeMap());
        string guard = scenario.GuardIds[0];
        var original = scenario.Commands.GetPrimary(guard)!;
        scenario.Commands.SuspendForEmergency(guard, "Sighting");
        Assert.That(scenario.Commands.GetPrimary(guard)!.Status, Is.EqualTo(OrderStatus.Suspended));
        Assert.That(scenario.DefenseFor(0), Is.EqualTo(0));
        Assert.That(scenario.Protect(MazeScenario.CommanderId, 0, 2), Is.True);
        Assert.That(scenario.Commands.GetOrder(original.Request.OrderId)!.Status, Is.EqualTo(OrderStatus.Superseded));
        Assert.That(scenario.DefenseFor(0), Is.EqualTo(2));
        scenario.Commands.ResumeAfterEmergency(guard);
        Assert.That(scenario.Commands.GetPrimary(guard)!.Status, Is.EqualTo(OrderStatus.Active));
        Assert.That(scenario.DefenseFor(0), Is.EqualTo(2));
    }

    [Test]
    public void CaptureResetClearsProgressAndRestoresCommanderAssignments()
    {
        var scenario = new MazeScenario(new Clock(), new MazeMap());
        scenario.Visit(MazeScenario.InfiltratorId, scenario.Checkpoints[0]);
        scenario.Protect(MazeScenario.CommanderId, 0, 2);
        scenario.Commands.SuspendForEmergency(scenario.GuardIds[0], "Noise");
        scenario.Reset();
        Assert.That(scenario.Reached, Is.Zero);
        Assert.That(scenario.DefenseFor(0), Is.Zero);
        Assert.That(scenario.Commands.GetPrimary(scenario.GuardIds[0])!.Status, Is.EqualTo(OrderStatus.Active));
        Assert.That(scenario.Commands.Orders.Count(o => !o.IsTerminal), Is.EqualTo(scenario.GuardIds.Count));
    }

    [Test]
    public void ForgedCheckpointOrRegionCannotBeDelivered()
    {
        var scenario = new MazeScenario(new Clock(), new MazeMap());
        var request = new OrderRequest("forged", scenario.GuardIds[0], OrderType.HoldArea,
            scenario.Checkpoints[0], "checkpoint-2", new OrderConstraints(regionId: "checkpoint-1"));
        Assert.That(scenario.Commands.Submit(MazeScenario.CommanderId, request).Accepted, Is.False);
        request = new OrderRequest("forged-region", scenario.GuardIds[0], OrderType.HoldArea,
            scenario.Checkpoints[0], "checkpoint-1", new OrderConstraints(regionId: "checkpoint-2"));
        Assert.That(scenario.Commands.Submit(MazeScenario.CommanderId, request).Accepted, Is.False);
    }
}
