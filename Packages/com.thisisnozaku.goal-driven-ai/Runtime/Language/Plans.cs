#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GoalDrivenAI.Language
{
    public enum PlanNodeKind { IssueOrder, SetPolicy, WaitForResult, ReportResult }
    public enum ResultPredicate
    {
        OrderCompleted, OrderFailed, SignalReceived, TimerElapsed, HoldPositionEstablished,
        Completed, Arrived, TargetVisible, TargetLost, SuppressionEstablished
    }
    public enum PlanFailurePolicy { AbortPlan, BlockDependents }
    public enum CommandPolicy { Engagement, Formation }
    public enum PolicyValue { HoldFire, ReturnFire, FireAtWill, Tight, Loose }
    public enum HandleKind { Actor, Location }

    public sealed class CatalogHandle
    {
        public string Id { get; }
        public HandleKind Kind { get; }
        public Position Position { get; }
        public string? RegionId { get; }
        public CatalogHandle(string id, HandleKind kind, Position position, string? regionId = null)
        {
            if (!Enum.IsDefined(typeof(HandleKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
            Id = RequireId(id); Kind = kind; Position = position;
            RegionId = regionId == null ? null : RequireId(regionId);
        }
        internal static string RequireId(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 96 ||
                id.Any(c => !(char.IsLetterOrDigit(c) || c == '_' || c == '-')))
                throw new ArgumentException("Invalid catalog identifier.");
            return id;
        }
    }

    public sealed class OrderDefinition
    {
        public string Name { get; }
        public bool RequiresTarget { get; }
        public bool Primary { get; }
        public double DefaultTimeout { get; }
        public PlanFailurePolicy FailurePolicy { get; }
        public OrderDefinition(string name, bool requiresTarget, bool primary,
            double defaultTimeout, PlanFailurePolicy failurePolicy)
        {
            Name = CatalogHandle.RequireId(name);
            if (!Enum.IsDefined(typeof(PlanFailurePolicy), failurePolicy) ||
                !Finite(defaultTimeout) || defaultTimeout <= 0 || defaultTimeout > 300)
                throw new ArgumentOutOfRangeException(nameof(defaultTimeout));
            RequiresTarget = requiresTarget; Primary = primary;
            DefaultTimeout = defaultTimeout; FailurePolicy = failurePolicy;
        }
        internal static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }

    public sealed class CommandCatalog
    {
        public string Version { get; }
        public IReadOnlyDictionary<string, OrderDefinition> Orders { get; }
        public CommandCatalog(string version, IEnumerable<OrderDefinition> orders)
        {
            Version = CatalogHandle.RequireId(version);
            Orders = new ReadOnlyDictionary<string, OrderDefinition>(
                orders.ToDictionary(o => o.Name, StringComparer.Ordinal));
            if (Orders.Count == 0 || Orders.Count > 64) throw new ArgumentException("Catalog size.");
        }
    }

    // Everything exposed to inference is a captured value, never a Transform or a live resolver.
    public sealed class CommandContext
    {
        public string Issuer { get; }
        public string RequestId { get; }
        public string Session { get; }
        public long Revision { get; }
        public IReadOnlyList<string> Recipients { get; }
        public IReadOnlyDictionary<string, CatalogHandle> Handles { get; }
        public string? AimedHandle { get; }
        public string? MarkerHandle { get; }
        public double CapturedAt { get; }
        public string SelectionKey => Issuer + ":" + string.Join(",", Recipients);

        public CommandContext(string issuer, string session, long revision, IEnumerable<string> recipients,
            IEnumerable<CatalogHandle> authorizedHandles, double capturedAt,
            string? aimedHandle = null, string? markerHandle = null, string? requestId = null)
        {
            Issuer = CatalogHandle.RequireId(issuer); Session = CatalogHandle.RequireId(session);
            RequestId = CatalogHandle.RequireId(requestId ?? Guid.NewGuid().ToString("N"));
            var selected = recipients.Select(CatalogHandle.RequireId).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (selected.Length == 0 || selected.Length > 8 || selected.Distinct().Count() != selected.Length ||
                !OrderDefinition.Finite(capturedAt) || revision < 0) throw new ArgumentException("Invalid context.");
            Recipients = Array.AsReadOnly(selected);
            Handles = new ReadOnlyDictionary<string, CatalogHandle>(
                authorizedHandles.ToDictionary(x => x.Id, StringComparer.Ordinal));
            if (Handles.Count > 128 || (aimedHandle != null && !Handles.ContainsKey(aimedHandle)) ||
                (markerHandle != null && !Handles.ContainsKey(markerHandle))) throw new ArgumentException("Unknown handle.");
            AimedHandle = aimedHandle; MarkerHandle = markerHandle; Revision = revision; CapturedAt = capturedAt;
        }
        internal CommandContext WithRequestId(string id) => new CommandContext(Issuer, Session, Revision, Recipients,
            Handles.Values, CapturedAt, AimedHandle, MarkerHandle, id);
        public string RequestKey => Issuer + ":" + RequestId;
    }

    public sealed class ValidatedPlanNode
    {
        public string Id { get; }
        public PlanNodeKind Kind { get; }
        public IReadOnlyList<string> Dependencies { get; }
        public IReadOnlyList<string> Recipients { get; }
        public OrderDefinition? Order { get; }
        public CatalogHandle? Target { get; }
        public CommandPolicy? Policy { get; }
        public PolicyValue? Value { get; }
        public ResultPredicate? Predicate { get; }
        public string? ObservedNode { get; }
        public string? Report { get; }
        public double Timeout { get; }
        public double? Duration { get; }
        public PlanFailurePolicy FailurePolicy { get; }

        internal ValidatedPlanNode(string id, PlanNodeKind kind, string[] dependencies, string[] recipients,
            OrderDefinition? order, CatalogHandle? target, CommandPolicy? policy, PolicyValue? value,
            ResultPredicate? predicate, string? observedNode, string? report, double timeout, PlanFailurePolicy failure, double? duration = null)
        {
            Id = id; Kind = kind; Dependencies = Array.AsReadOnly(dependencies); Recipients = Array.AsReadOnly(recipients);
            Order = order; Target = target; Policy = policy; Value = value; Predicate = predicate;
            ObservedNode = observedNode; Report = report; Timeout = timeout; FailurePolicy = failure;
            Duration = duration;
        }
    }

    public sealed class ValidatedCommandPlan
    {
        public const int SchemaVersion = 1;
        public string CatalogVersion { get; }
        public CommandContext Context { get; }
        public string RequestId => Context.RequestId;
        public IReadOnlyList<ValidatedPlanNode> Nodes { get; }
        public string CanonicalJson { get; }
        public string Hash { get; }
        internal ValidatedCommandPlan(string version, CommandContext context, ValidatedPlanNode[] nodes)
        {
            CatalogVersion = version; Context = context; Nodes = Array.AsReadOnly(nodes);
            CanonicalJson = PlanCompiler.Canonical(this).ToString(Formatting.None);
            using var sha = SHA256.Create();
            Hash = string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(CanonicalJson)).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
        }
    }

    public sealed class PlanCompilation
    {
        public ValidatedCommandPlan? Plan { get; }
        public string? Error { get; }
        public bool Success => Plan != null;
        internal PlanCompilation(ValidatedCommandPlan? plan, string? error) { Plan = plan; Error = error; }
    }

    public class PlanCompiler
    {
        public const int MaxOutputCharacters = 32768;
        private readonly CommandCatalog catalog;
        public CommandCatalog Catalog => catalog;
        public PlanCompiler(CommandCatalog catalog) { this.catalog = catalog; }

        // Save games retain the canonical JSON and hash, not a new model proposal.
        public PlanCompilation Restore(string canonicalJson, string expectedHash)
        {
            try
            {
                if (canonicalJson.Length > MaxOutputCharacters * 2) Fail("Saved plan size.");
                StrictJson.Validate(canonicalJson);
                var saved = JObject.Parse(canonicalJson, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                var c = (JObject)saved["context"]!;
                var handles = ((JArray)c["handles"]!).Select(t => {
                    var h = (JObject)t;
                    return new CatalogHandle(String(h, "id"), EnumValue<HandleKind>(h, "kind"),
                        new Position((float)h["x"]!, (float)h["y"]!, (float)h["z"]!),
                        h["regionId"]?.Type == JTokenType.Null ? null : String(h, "regionId"));
                }).ToArray();
                var context = new CommandContext(String(c, "issuer"), String(c, "session"), (long)c["revision"]!,
                    Strings(c, "recipients", 8), handles, (double)c["capturedAt"]!,
                    c["aimedHandle"]?.Type == JTokenType.Null ? null : String(c, "aimedHandle"),
                    c["markerHandle"]?.Type == JTokenType.Null ? null : String(c, "markerHandle"), String(c, "requestId"));
                var nodes = (JArray)saved["nodes"]!.DeepClone();
                foreach (JObject node in nodes)
                {
                    var kind = EnumValue<PlanNodeKind>(node, "type");
                    foreach (var p in node.Properties().Where(p => p.Value.Type == JTokenType.Null ||
                        (p.Name == "recipients" && kind != PlanNodeKind.IssueOrder && kind != PlanNodeKind.SetPolicy)).ToArray()) p.Remove();
                }
                var result = Compile(new JObject { ["schemaVersion"] = saved["schemaVersion"],
                    ["catalogVersion"] = saved["catalogVersion"], ["nodes"] = nodes }.ToString(Formatting.None), context);
                if (!result.Success || result.Plan!.Hash != expectedHash || result.Plan.CanonicalJson != canonicalJson)
                    return new PlanCompilation(null, "Saved plan binding mismatch.");
                return result;
            }
            catch (Exception ex) when (ex is JsonException || ex is ArgumentException || ex is InvalidCastException ||
                ex is NullReferenceException || ex is OverflowException || ex is FormatException)
            { return new PlanCompilation(null, "Invalid saved plan."); }
        }

        public PlanCompilation Compile(string json, CommandContext context)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(json) || json.Length > MaxOutputCharacters) Fail("Output size.");
                StrictJson.Validate(json);
                using var text = new System.IO.StringReader(json);
                using var reader = new JsonTextReader(text) { MaxDepth = 16, DateParseHandling = DateParseHandling.None };
                var root = JObject.Load(reader, new JsonLoadSettings {
                    DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
                    CommentHandling = CommentHandling.Load
                });
                if (root.Descendants().Any(t => t.Type == JTokenType.Comment) || reader.Read()) Fail("Only one JSON object is allowed.");
                Fields(root, "schemaVersion", "catalogVersion", "nodes");
                if (root["schemaVersion"]?.Type != JTokenType.Integer ||
                    (long)root["schemaVersion"]! != ValidatedCommandPlan.SchemaVersion) Fail("Schema version.");
                if (String(root, "catalogVersion") != catalog.Version) Fail("Catalog version.");
                if (!(root["nodes"] is JArray array) || array.Count == 0 || array.Count > 16) Fail("Node count.");
                var nodes = ((JArray)root["nodes"]!).Select(t => ParseNode(t, context)).ToArray();
                if (nodes.Select(n => n.Id).Distinct(StringComparer.Ordinal).Count() != nodes.Length) Fail("Duplicate node id.");
                var map = nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
                foreach (var node in nodes)
                {
                    if (node.Dependencies.Any(d => !map.ContainsKey(d) || d == node.Id)) Fail("Dangling or self dependency.");
                    if (node.ObservedNode != null && (!map.TryGetValue(node.ObservedNode, out var observed) ||
                        observed.Kind != PlanNodeKind.IssueOrder || !Ancestors(node, map).Contains(node.ObservedNode)))
                        Fail("Wait must observe a preceding order.");
                }
                var sorted = new List<ValidatedPlanNode>();
                while (sorted.Count < nodes.Length)
                {
                    var next = nodes.Where(n => !sorted.Contains(n) && n.Dependencies.All(d => sorted.Any(s => s.Id == d)))
                        .OrderBy(n => n.Id, StringComparer.Ordinal).FirstOrDefault();
                    if (next == null) Fail("Dependency cycle.");
                    sorted.Add(next!);
                }
                for (int i = 0; i < nodes.Length; i++)
                    for (int j = i + 1; j < nodes.Length; j++)
                        if (nodes[i].Order?.Primary == true && nodes[j].Order?.Primary == true &&
                            nodes[i].Recipients.Intersect(nodes[j].Recipients).Any() &&
                            !CompletionSequenced(nodes[i], nodes[j], map) && !CompletionSequenced(nodes[j], nodes[i], map))
                            Fail("Conflicting unsequenced primary orders.");
                return new PlanCompilation(new ValidatedCommandPlan(catalog.Version, context, sorted.ToArray()), null);
            }
            catch (Exception ex) when (ex is JsonException || ex is ArgumentException || ex is InvalidOperationException ||
                ex is OverflowException || ex is FormatException)
            { return new PlanCompilation(null, ex.Message); }
        }

        private ValidatedPlanNode ParseNode(JToken token, CommandContext context)
        {
            if (!(token is JObject obj)) throw new ArgumentException("Node must be an object.");
            var kind = EnumValue<PlanNodeKind>(obj, "type");
            var allowed = new List<string> { "id", "type", "dependsOn", "timeout", "failurePolicy" };
            switch (kind)
            {
                case PlanNodeKind.IssueOrder: allowed.AddRange(new[] { "recipients", "order", "target" }); break;
                case PlanNodeKind.SetPolicy: allowed.AddRange(new[] { "recipients", "policy", "value" }); break;
                case PlanNodeKind.WaitForResult: allowed.AddRange(new[] { "observedNode", "predicate", "duration" }); break;
                case PlanNodeKind.ReportResult: allowed.Add("report"); break;
            }
            Fields(obj, allowed.ToArray());
            var id = CatalogHandle.RequireId(String(obj, "id"));
            var deps = Strings(obj, "dependsOn", 16);
            var recipients = kind == PlanNodeKind.IssueOrder || kind == PlanNodeKind.SetPolicy ? Strings(obj, "recipients", 8) : Array.Empty<string>();
            if ((kind == PlanNodeKind.IssueOrder || kind == PlanNodeKind.SetPolicy) &&
                (recipients.Length == 0 || recipients.Except(context.Recipients).Any())) Fail("Unauthorized recipients.");
            OrderDefinition? order = null; CatalogHandle? target = null;
            CommandPolicy? policy = null; PolicyValue? value = null;
            ResultPredicate? predicate = null; string? observed = null; string? report = null; double? duration = null;
            if (kind == PlanNodeKind.IssueOrder)
            {
                if (!catalog.Orders.TryGetValue(String(obj, "order"), out order)) Fail("Unknown order.");
                if (obj["target"] != null && !context.Handles.TryGetValue(String(obj, "target"), out target)) Fail("Unknown or unauthorized target.");
                if (order!.RequiresTarget != (target != null)) Fail("Order target mismatch.");
            }
            if (kind == PlanNodeKind.SetPolicy)
            {
                policy = EnumValue<CommandPolicy>(obj, "policy"); value = EnumValue<PolicyValue>(obj, "value");
                if ((policy == CommandPolicy.Engagement) != (value == PolicyValue.HoldFire || value == PolicyValue.ReturnFire || value == PolicyValue.FireAtWill))
                    Fail("Policy value mismatch.");
            }
            if (kind == PlanNodeKind.WaitForResult)
            {
                predicate = EnumValue<ResultPredicate>(obj, "predicate");
                if (obj["observedNode"] != null) observed = CatalogHandle.RequireId(String(obj, "observedNode"));
                if (observed == null && predicate != ResultPredicate.SignalReceived && predicate != ResultPredicate.TimerElapsed)
                    Fail("Result predicate requires an observed order.");
                if (predicate == ResultPredicate.TimerElapsed)
                {
                    if (obj["duration"]?.Type != JTokenType.Integer && obj["duration"]?.Type != JTokenType.Float)
                        Fail("Timer requires a duration.");
                    duration = (double)obj["duration"]!;
                }
                else if (obj["duration"] != null) Fail("Only a timer accepts duration.");
            }
            if (kind == PlanNodeKind.ReportResult)
            {
                report = String(obj, "report");
                if (report.Length > 256 || report.Any(char.IsControl)) Fail("Invalid report.");
            }
            double timeout = order?.DefaultTimeout ?? 10;
            if (obj["timeout"] != null)
            {
                if (obj["timeout"]!.Type != JTokenType.Integer && obj["timeout"]!.Type != JTokenType.Float) Fail("Invalid timeout.");
                timeout = (double)obj["timeout"]!;
            }
            if (!OrderDefinition.Finite(timeout) || timeout <= 0 || timeout > 300) Fail("Invalid timeout.");
            if (duration.HasValue && (!OrderDefinition.Finite(duration.Value) || duration <= 0 || duration >= timeout)) Fail("Invalid timer duration.");
            var failure = obj["failurePolicy"] == null ? order?.FailurePolicy ?? PlanFailurePolicy.BlockDependents :
                EnumValue<PlanFailurePolicy>(obj, "failurePolicy");
            // Inference cannot weaken an authored order failure policy.
            if (order != null && failure != order.FailurePolicy) Fail("Authored failure policy mismatch.");
            return new ValidatedPlanNode(id, kind, deps, recipients, order, target, policy, value, predicate, observed, report, timeout, failure, duration);
        }

        private static HashSet<string> Ancestors(ValidatedPlanNode node, Dictionary<string, ValidatedPlanNode> map)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Stack<string>(node.Dependencies);
            while (pending.Count != 0)
            {
                var id = pending.Pop();
                if (result.Add(id) && map.TryGetValue(id, out var dependency))
                    foreach (var parent in dependency.Dependencies) pending.Push(parent);
            }
            return result;
        }
        private static bool CompletionSequenced(ValidatedPlanNode earlier, ValidatedPlanNode later,
            Dictionary<string, ValidatedPlanNode> map)
        {
            return Ancestors(later, map).Select(id => map[id]).Any(n =>
                n.Kind == PlanNodeKind.WaitForResult && n.ObservedNode == earlier.Id &&
                (n.Predicate == ResultPredicate.OrderCompleted || n.Predicate == ResultPredicate.Completed));
        }
        private static void Fail(string message) => throw new ArgumentException(message);
        private static void Fields(JObject obj, params string[] fields)
        {
            if (obj.Properties().Any(p => !fields.Contains(p.Name, StringComparer.Ordinal))) Fail("Unknown field.");
        }
        private static string String(JObject obj, string field)
        {
            if (obj[field]?.Type != JTokenType.String) throw new ArgumentException("Expected string: " + field);
            return (string)obj[field]!;
        }
        private static T EnumValue<T>(JObject obj, string field) where T : struct, Enum
        {
            var value = String(obj, field);
            if (!Enum.GetNames(typeof(T)).Contains(value, StringComparer.Ordinal)) throw new ArgumentException("Unknown enum: " + field);
            return (T)Enum.Parse(typeof(T), value, false);
        }
        private static string[] Strings(JObject obj, string field, int maximum)
        {
            if (!(obj[field] is JArray values) || values.Count > maximum) throw new ArgumentException("Expected bounded array: " + field);
            var result = values.Select(t => t.Type == JTokenType.String ? CatalogHandle.RequireId((string)t!) :
                throw new ArgumentException("Expected identifier.")).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (result.Distinct(StringComparer.Ordinal).Count() != result.Length) Fail("Duplicate array entry.");
            return result;
        }

        internal static JObject Canonical(ValidatedCommandPlan plan)
        {
            var context = plan.Context;
            return new JObject {
                ["schemaVersion"] = ValidatedCommandPlan.SchemaVersion, ["catalogVersion"] = plan.CatalogVersion,
                ["context"] = new JObject {
                    ["issuer"] = context.Issuer, ["requestId"] = context.RequestId, ["session"] = context.Session, ["revision"] = context.Revision,
                    ["capturedAt"] = context.CapturedAt, ["recipients"] = new JArray(context.Recipients),
                    ["aimedHandle"] = context.AimedHandle, ["markerHandle"] = context.MarkerHandle,
                    ["handles"] = new JArray(context.Handles.Values.OrderBy(h => h.Id, StringComparer.Ordinal).Select(h =>
                        new JObject { ["id"] = h.Id, ["kind"] = h.Kind.ToString(), ["x"] = h.Position.X, ["y"] = h.Position.Y,
                            ["z"] = h.Position.Z, ["regionId"] = h.RegionId }))
                },
                ["nodes"] = new JArray(plan.Nodes.Select(n => new JObject {
                    ["id"] = n.Id, ["type"] = n.Kind.ToString(), ["dependsOn"] = new JArray(n.Dependencies),
                    ["recipients"] = new JArray(n.Recipients), ["order"] = n.Order?.Name,
                    ["target"] = n.Target?.Id, ["policy"] = n.Policy?.ToString(), ["value"] = n.Value?.ToString(),
                    ["observedNode"] = n.ObservedNode, ["predicate"] = n.Predicate?.ToString(), ["report"] = n.Report,
                    ["timeout"] = n.Timeout, ["failurePolicy"] = n.FailurePolicy.ToString(), ["duration"] = n.Duration
                }))
            };
        }
    }
    public sealed class CommandPlanCompiler : PlanCompiler
    {
        public CommandPlanCompiler(CommandCatalog catalog) : base(catalog) { }
    }
}
