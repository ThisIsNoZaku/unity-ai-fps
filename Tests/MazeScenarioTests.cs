using GoalDrivenAI;
using GoalDrivenAI.Commands;
using GoalDrivenAI.Stealth;
using GoalDrivenAI.Language;

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
    public void NamedLocationCatalogDrivesDefenseOrdersAndObjectiveProgress()
    {
        var scenario = new MazeScenario(new Clock(), new MazeMap());
        Assert.That(scenario.Locations.Count, Is.EqualTo(scenario.CheckpointLocations.Count + 1));
        Assert.That(scenario.ExitLocation.Id, Is.EqualTo("exit"));
        Assert.That(scenario.Locations["exit"], Is.SameAs(scenario.ExitLocation));
        Assert.That(scenario.ExitLocation.Kind, Is.EqualTo(HandleKind.Location));
        Assert.That(scenario.ExitLocation.RegionId, Is.Null);
        for (int i = 0; i < scenario.CheckpointLocations.Count; i++)
        {
            var location = scenario.CheckpointLocations[i];
            Assert.That(location.Id, Is.EqualTo("checkpoint-" + (i + 1)));
            Assert.That(location.Kind, Is.EqualTo(HandleKind.Location));
            Assert.That(location.RegionId, Is.EqualTo(location.Id));
            Assert.That(scenario.Locations[location.Id], Is.SameAs(location));
            Assert.That(scenario.NextObjective, Is.SameAs(location));
            Assert.That(scenario.Protect(MazeScenario.CommanderId, 0, i), Is.True);
            var order = scenario.Commands.GetPrimary(scenario.GuardIds[0])!.Request;
            Assert.That(order.TargetId, Is.EqualTo(location.Id));
            Assert.That(order.Position, Is.EqualTo(location.Position));
            Assert.That(order.Constraints.RegionId, Is.EqualTo(location.RegionId));
            Assert.That(scenario.Visit(MazeScenario.InfiltratorId, location.Position), Is.True);
        }
        Assert.That(scenario.NextObjective, Is.SameAs(scenario.ExitLocation));
        Assert.That(scenario.CanEscape(MazeScenario.InfiltratorId, scenario.NextObjective.Position), Is.True);
        var first = scenario.CheckpointLocations[0];
        // The exit is a path destination, not an authorized checkpoint defense region.
        Assert.That(scenario.Commands.Submit(MazeScenario.CommanderId,
            new OrderRequest("exit-defense", scenario.GuardIds[0], OrderType.HoldArea,
                scenario.ExitLocation.Position, scenario.ExitLocation.Id,
                new OrderConstraints(regionId: first.RegionId))).Accepted, Is.False);
        Assert.That(scenario.Commands.Submit(MazeScenario.CommanderId,
            new OrderRequest("forged-location", scenario.GuardIds[0], OrderType.HoldArea,
                scenario.ExitLocation.Position, first.Id,
                new OrderConstraints(regionId: first.RegionId))).Accepted, Is.False);
        scenario.Reset();
        Assert.That(scenario.NextObjective, Is.SameAs(first));
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
    public void OnlyCommanderCanReleaseCheckpointDefenseForPatrol()
    {
        var scenario = new MazeScenario(new Clock(), new MazeMap());
        Assert.That(scenario.Patrol("observer", 0), Is.False);
        Assert.That(scenario.Patrol(MazeScenario.InfiltratorId, 0), Is.False);
        Assert.That(scenario.Patrol(scenario.GuardIds[0], 0), Is.False);
        Assert.That(scenario.Patrol(MazeScenario.CommanderId, -1), Is.False);
        var order = scenario.Commands.GetPrimary(scenario.GuardIds[0])!;
        Assert.That(scenario.Patrol(MazeScenario.CommanderId, 0), Is.True);
        Assert.That(scenario.Commands.GetOrder(order.Request.OrderId)!.Status, Is.EqualTo(OrderStatus.Cancelled));
        Assert.That(scenario.DefenseFor(0), Is.EqualTo(-1));
        Assert.That(scenario.Patrol(MazeScenario.CommanderId, 0), Is.True);
        Assert.That(scenario.Protect(MazeScenario.CommanderId, 0, 2), Is.True);
        Assert.That(scenario.DefenseFor(0), Is.EqualTo(2));
    }

    [Test]
    public void PatrolPlanAdvancesOnlyThroughHostVerifiedMoveCompletions()
    {
        var scenario = new MazeScenario(new Clock(), new MazeMap());
        Assert.That(scenario.Patrol(MazeScenario.CommanderId, 0, dwellSeconds: 0), Is.True);
        var first = scenario.Commands.GetPrimary(scenario.GuardIds[0])!;
        Assert.That(first.Request.Type, Is.EqualTo(OrderType.Move));
        Assert.That(first.Request.TargetId, Is.EqualTo("checkpoint-1"));
        Assert.That(scenario.Commands.ReportCompletion(first.Request.OrderId, "inference says arrived").Accepted, Is.False);
        Assert.That(scenario.CompleteNavigation(0, scenario.Exit), Is.False);
        for (int i = 0; i < 7; i++)
        {
            var active = scenario.Commands.GetPrimary(scenario.GuardIds[0])!;
            Assert.That(active.Request.TargetId, Is.EqualTo("checkpoint-" + (i % 3 + 1)));
            Assert.That(scenario.CompleteNavigation(0, active.Request.Position!.Value), Is.True);
            scenario.PumpPlans();
            if (i % 3 == 2) scenario.PumpPlans();
        }
        Assert.That(scenario.PatrolVisits(0), Is.EqualTo(7));
        Assert.That(scenario.Protect(MazeScenario.CommanderId, 0, 2), Is.True);
        Assert.That(scenario.IsPatrolling(0), Is.False);
        scenario.PumpPlans();
        Assert.That(scenario.Commands.GetPrimary(scenario.GuardIds[0])!.Request.Type, Is.EqualTo(OrderType.HoldArea));
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
