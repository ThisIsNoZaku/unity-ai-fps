using GoalDrivenAI.Stealth;

namespace GoalDrivenAI.Tests;

public sealed class StealthMazeTests
{
    [TestCase(0)] [TestCase(42)] [TestCase(1234)] [TestCase(-1)]
    public void GeneratedMazeHasConnectedPatrolCellsAndPlayerOnlyShortcuts(int seed)
    {
        var map = new MazeMap(seed);
        int crawls = 0;
        for (int y = 0; y < map.Height; y++) for (int x = 0; x < map.Width; x++)
        {
            if (x == 0 || y == 0 || x == map.Width - 1 || y == map.Height - 1)
                Assert.That(map[x, y], Is.EqualTo('#'));
            if (map.Walkable(x, y) && (x, y) != (1, 1))
            {
                var route = map.Route((1, 1), (x, y));
                Assert.That(route.Last(), Is.EqualTo((x, y)));
                var previous = (x: 1, y: 1);
                foreach (var step in route)
                {
                    Assert.That(Math.Abs(step.x - previous.x) + Math.Abs(step.y - previous.y), Is.EqualTo(1));
                    Assert.That(map[step.x, step.y], Is.EqualTo('.')); previous = step;
                }
            }
            if (map[x, y] != 'c') continue;
            crawls++;
            Assert.That(map.Walkable(x, y), Is.False);
            Assert.That(map.Walkable(x, y, true), Is.True);
            var playerRoute = map.Route((1, 1), (x, y), true);
            Assert.That(playerRoute.Last(), Is.EqualTo((x, y)));
            var guardRoute = map.Route((1, 1), (x, y));
            Assert.That(guardRoute.Last(), Is.Not.EqualTo((x, y)));
            Assert.That(Math.Abs(guardRoute.Last().x - x) + Math.Abs(guardRoute.Last().y - y), Is.EqualTo(1));
        }
        Assert.That(crawls, Is.GreaterThan(0));
    }

    [Test]
    public void SeedIsRepeatableAndExitHasOrdinaryRoute()
    {
        var a = new MazeMap(); var b = new MazeMap();
        for (int y = 0; y < a.Height; y++) for (int x = 0; x < a.Width; x++) Assert.That(a[x, y], Is.EqualTo(b[x, y]));
        Assert.That(a.Route((1, 1), (a.Width - 2, a.Height - 2)).Last(), Is.EqualTo((a.Width - 2, a.Height - 2)));
        Assert.That(a.Route((0, 0), (1, 1)), Is.Empty);
    }

    [Test]
    public void HearingRespectsListenerRangeSourceLoudnessAndSensitivity()
    {
        Assert.That(Audibility.Strength(5, 10, 20, 1), Is.EqualTo(.5));
        Assert.That(Audibility.Strength(5, 10, 20, 2), Is.EqualTo(.75));
        Assert.That(Audibility.Strength(5, 10, 4, 2), Is.Zero);
        Assert.That(Audibility.Strength(10, 10, 20, 1), Is.Zero);
        Assert.That(Audibility.Strength(0, 10, 20, 0), Is.Zero);
        Assert.That(Audibility.Strength(0, 0, 20, 1), Is.Zero);
        Assert.That(Audibility.Strength(double.NaN, 10, 20, 1), Is.Zero);
        Assert.That(Audibility.Strength(0, 10, double.PositiveInfinity, 1), Is.Zero);
    }
}
