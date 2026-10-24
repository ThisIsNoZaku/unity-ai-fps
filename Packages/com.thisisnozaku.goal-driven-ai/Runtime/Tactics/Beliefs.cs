using System;
using System.Collections.Generic;
using System.Linq;

namespace GoalDrivenAI.Tactics
{
    public enum Truth { Unknown, False, True }
    public enum EvidenceKind { Vision, Hearing, Communication }

    /// <summary>A copied observation, never a reference to a moving world object.</summary>
    public sealed class Evidence
    {
        public string ReportId { get; }
        public string Fact { get; }
        public string? SubjectId { get; }
        public EvidenceKind Kind { get; }
        public Truth Value { get; }
        public Position? Position { get; }
        public double ObservedAt { get; }
        public double ExpiresAt { get; }
        public double Confidence { get; }
        public string? SourceId { get; }
        public double ReceiptAt { get; }
        public double InitialUncertaintyRadius { get; }
        public double UncertaintyGrowthRate { get; }

        public Evidence(string reportId, string fact, string? subjectId, EvidenceKind kind,
            Truth value, Position? position, double observedAt, double lifetime, double confidence = 1,
            string? sourceId = null, double? receiptAt = null, double initialUncertaintyRadius = 0,
            double uncertaintyGrowthRate = 1)
        {
            if (string.IsNullOrWhiteSpace(reportId) || string.IsNullOrWhiteSpace(fact))
                throw new ArgumentException("Report and fact IDs are required.");
            if (!Finite(observedAt) || !Finite(lifetime) || lifetime <= 0 || !Finite(observedAt + lifetime))
                throw new ArgumentOutOfRangeException(nameof(lifetime));
            if (!Finite(confidence) || confidence < 0 || confidence > 1)
                throw new ArgumentOutOfRangeException(nameof(confidence));
            if (!Finite(receiptAt ?? observedAt) || (receiptAt ?? observedAt) < observedAt)
                throw new ArgumentOutOfRangeException(nameof(receiptAt));
            if (!Finite(initialUncertaintyRadius) || initialUncertaintyRadius < 0 ||
                !Finite(uncertaintyGrowthRate) || uncertaintyGrowthRate < 0)
                throw new ArgumentOutOfRangeException(nameof(initialUncertaintyRadius));
            if (!Enum.IsDefined(typeof(EvidenceKind), kind) || !Enum.IsDefined(typeof(Truth), value))
                throw new ArgumentOutOfRangeException(nameof(kind));
            ReportId = reportId;
            Fact = fact;
            // A sound is a stimulus, not authenticated knowledge of its emitter.
            SubjectId = kind == EvidenceKind.Hearing ? null : subjectId;
            Kind = kind;
            Value = value;
            Position = position;
            ObservedAt = observedAt;
            ExpiresAt = observedAt + lifetime;
            Confidence = confidence;
            SourceId = sourceId;
            ReceiptAt = receiptAt ?? observedAt;
            InitialUncertaintyRadius = initialUncertaintyRadius;
            UncertaintyGrowthRate = uncertaintyGrowthRate;
        }

        internal static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }

    public readonly struct Belief
    {
        public Truth Value { get; }
        public Evidence? Report { get; }
        public double Confidence { get; }
        public double Uncertainty => 1 - Confidence;
        public double UncertaintyRadius { get; }
        public bool SearchComplete { get; }
        public bool CanSearch => Report?.Position != null && !SearchComplete;
        internal Belief(Evidence? report, double now, bool searched)
        {
            Report = report;
            Value = report?.Value ?? Truth.Unknown;
            Confidence = report == null ? 0 : report.Confidence *
                Math.Max(0, (report.ExpiresAt - now) / (report.ExpiresAt - report.ObservedAt));
            UncertaintyRadius = report?.Position == null ? double.PositiveInfinity :
                report.InitialUncertaintyRadius + Math.Max(0, now - report.ObservedAt) * report.UncertaintyGrowthRate;
            SearchComplete = searched;
        }
    }

    public sealed class BeliefStore
    {
        private readonly IClock clock;
        private readonly int capacity;
        private readonly Dictionary<string, Evidence> reports = new Dictionary<string, Evidence>(StringComparer.Ordinal);
        private readonly HashSet<string> searched = new HashSet<string>(StringComparer.Ordinal);
        public BeliefStore(IClock clock, int capacity = 128)
        {
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            this.capacity = capacity;
        }

        public int Count { get { Expire(); return reports.Count; } }
        public bool Add(Evidence report)
        {
            if (report == null) throw new ArgumentNullException(nameof(report));
            Expire();
            if (report.ObservedAt > clock.Now || report.ReceiptAt > clock.Now ||
                report.ExpiresAt <= clock.Now || reports.ContainsKey(report.ReportId))
                return false;
            if (reports.Count == capacity)
            {
                var oldest = reports.Values.OrderBy(r => r.ObservedAt).ThenBy(r => r.ReportId, StringComparer.Ordinal).First();
                // Do not let an old relayed report displace more recent knowledge.
                if (report.ObservedAt < oldest.ObservedAt) return false;
                reports.Remove(oldest.ReportId);
                searched.Remove(oldest.ReportId);
            }
            reports.Add(report.ReportId, report);
            return true;
        }

        public Belief Query(string fact, string? subjectId = null)
        {
            Expire();
            var report = reports.Values.Where(r => r.Fact == fact && r.SubjectId == subjectId)
                .OrderByDescending(r => r.ObservedAt).ThenByDescending(r => r.Confidence)
                .ThenBy(r => r.ReportId, StringComparer.Ordinal).FirstOrDefault();
            return new Belief(report, clock.Now, report != null && searched.Contains(report.ReportId));
        }

        public Belief LastKnownPosition(string subjectId)
        {
            if (string.IsNullOrWhiteSpace(subjectId)) throw new ArgumentException("Subject ID is required.", nameof(subjectId));
            Expire();
            var report = reports.Values.Where(r => r.SubjectId == subjectId && r.Position.HasValue)
                .OrderByDescending(r => r.ObservedAt).ThenBy(r => r.ReportId, StringComparer.Ordinal).FirstOrDefault();
            return new Belief(report, clock.Now, report != null && searched.Contains(report.ReportId));
        }

        public bool CompleteSearch(string reportId)
        {
            Expire();
            return reports.TryGetValue(reportId, out var report) && report.Position.HasValue && searched.Add(reportId);
        }

        public IReadOnlyList<Evidence> Snapshot()
        {
            Expire();
            return Array.AsReadOnly(reports.Values.OrderBy(r => r.ReportId, StringComparer.Ordinal).ToArray());
        }

        private void Expire()
        {
            foreach (var id in reports.Values.Where(r => r.ExpiresAt <= clock.Now).Select(r => r.ReportId).ToArray())
            {
                reports.Remove(id);
                searched.Remove(id);
            }
        }
    }
}
