using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using GoalDrivenAI.Language;

namespace GoalDrivenAI.Navigation
{
    /// <summary>An immutable snapshot of named locations. Game code owns which entries are authorized.</summary>
    public sealed class LocationRegistry : IReadOnlyDictionary<string, CatalogHandle>
    {
        private readonly IReadOnlyDictionary<string, CatalogHandle> entries;
        public LocationRegistry(IEnumerable<CatalogHandle> locations)
        {
            if (locations == null) throw new ArgumentNullException(nameof(locations));
            var copy = new Dictionary<string, CatalogHandle>(StringComparer.Ordinal);
            foreach (var location in locations)
            {
                if (location == null || location.Kind != HandleKind.Location)
                    throw new ArgumentException("Registry entries must be location handles.", nameof(locations));
                if (copy.ContainsKey(location.Id))
                    throw new ArgumentException("Duplicate location: " + location.Id, nameof(locations));
                copy.Add(location.Id, location);
            }
            entries = new ReadOnlyDictionary<string, CatalogHandle>(copy);
        }
        public CatalogHandle this[string id] => entries[id];
        public int Count => entries.Count;
        public IEnumerable<string> Keys => entries.Keys;
        public IEnumerable<CatalogHandle> Values => entries.Values;
        public bool ContainsKey(string id) => entries.ContainsKey(id);
        public bool TryGetValue(string id, out CatalogHandle location) => entries.TryGetValue(id, out location!);
        public IEnumerator<KeyValuePair<string, CatalogHandle>> GetEnumerator() => entries.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
