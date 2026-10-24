using System;

namespace GoalDrivenAI
{
    public interface IClock
    {
        double Now { get; }
    }

    public readonly struct Position : IEquatable<Position>
    {
        public readonly float X;
        public readonly float Y;
        public readonly float Z;

        public Position(float x, float y, float z)
        {
            if (!Finite(x) || !Finite(y) || !Finite(z))
                throw new ArgumentOutOfRangeException(nameof(x), "Position must be finite.");
            X = x;
            Y = y;
            Z = z;
        }

        public double DistanceTo(Position other)
        {
            double x = (double)X - other.X, y = (double)Y - other.Y, z = (double)Z - other.Z;
            return Math.Sqrt(x * x + y * y + z * z);
        }

        public bool Equals(Position other) => X.Equals(other.X) && Y.Equals(other.Y) && Z.Equals(other.Z);
        public override bool Equals(object? obj) => obj is Position other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(X, Y, Z);
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
