using System;
using System.Collections.Generic;

namespace GoalDrivenAI.Stealth
{
    /// <summary>Deterministic demo topology. Crawl cells are traversable only by the player.</summary>
    public sealed class MazeMap
    {
        private readonly char[,] cells;
        public int Width { get; }
        public int Height { get; }
        public char this[int x, int y] => x < 0 || y < 0 || x >= Width || y >= Height ? '#' : cells[x, y];
        public bool Walkable(int x, int y, bool canCrawl = false) => this[x, y] == '.' || (canCrawl && this[x, y] == 'c');
        private static readonly (int x, int y)[] Directions = { (1, 0), (0, 1), (-1, 0), (0, -1) };

        public MazeMap(int seed = 42, int width = 13, int height = 11)
        {
            if (width < 7 || height < 7 || width % 2 == 0 || height % 2 == 0)
                throw new ArgumentOutOfRangeException(nameof(width), "Use odd dimensions of at least seven.");
            Width = width; Height = height; cells = new char[width, height];
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) cells[x, y] = '#';
            var random = new Random(seed);
            var stack = new Stack<(int x, int y)>();
            cells[1, 1] = '.'; stack.Push((1, 1));
            while (stack.Count > 0)
            {
                var p = stack.Peek();
                var options = new List<(int x, int y)>();
                foreach (var d in Directions)
                {
                    int x = p.x + d.x * 2, y = p.y + d.y * 2;
                    if (x > 0 && y > 0 && x < width - 1 && y < height - 1 && cells[x, y] == '#') options.Add((x, y));
                }
                if (options.Count == 0) { stack.Pop(); continue; }
                var next = options[random.Next(options.Count)];
                cells[(p.x + next.x) / 2, (p.y + next.y) / 2] = '.';
                cells[next.x, next.y] = '.'; stack.Push(next);
            }
            // Add distributed shortcuts without cutting the outer boundary or removing the ordinary route.
            int crawlCount = 0;
            for (int y = 2; y < height - 2 && crawlCount < 4; y++)
                for (int x = 2; x < width - 2 && crawlCount < 4; x++)
                    if (cells[x, y] == '#' && ((Walkable(x - 1, y) && Walkable(x + 1, y)) ^
                        (Walkable(x, y - 1) && Walkable(x, y + 1))))
                    { cells[x, y] = 'c'; crawlCount++; x += 2; }
        }

        /// <summary>BFS to a reachable cell closest to a captured destination, including crawl-only destinations.</summary>
        public IReadOnlyList<(int x, int y)> Route((int x, int y) start, (int x, int y) destination, bool canCrawl = false)
        {
            var result = new List<(int x, int y)>();
            if (!Walkable(start.x, start.y, canCrawl)) return result;
            var parents = new Dictionary<(int x, int y), (int x, int y)> { [start] = start };
            var queue = new Queue<(int x, int y)>(); queue.Enqueue(start);
            var closest = start;
            int Distance((int x, int y) p) => Math.Abs(p.x - destination.x) + Math.Abs(p.y - destination.y);
            while (queue.Count > 0)
            {
                var p = queue.Dequeue();
                if (Distance(p) < Distance(closest)) closest = p;
                if (p == destination) { closest = p; break; }
                foreach (var d in Directions)
                {
                    var next = (x: p.x + d.x, y: p.y + d.y);
                    if (!Walkable(next.x, next.y, canCrawl) || parents.ContainsKey(next)) continue;
                    parents[next] = p; queue.Enqueue(next);
                }
            }
            for (var p = closest; p != start; p = parents[p]) result.Add(p);
            result.Reverse(); return result;
        }
    }

    public static class Audibility
    {
        /// <summary>Range caps reception; sensitivity scales the source's falloff radius. Zero disables hearing.</summary>
        public static double Strength(double distance, double sourceRadius, double range, double sensitivity)
        {
            if (!Finite(distance) || !Finite(sourceRadius) || !Finite(range) || !Finite(sensitivity) ||
                distance < 0 || sourceRadius <= 0 || range <= 0 || sensitivity <= 0 || distance > range) return 0;
            return Math.Max(0, 1 - distance / (sourceRadius * sensitivity));
        }
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
