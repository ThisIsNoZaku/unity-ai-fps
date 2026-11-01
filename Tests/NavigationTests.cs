using System;
using System.Collections.Generic;
using GoalDrivenAI;
using GoalDrivenAI.Language;
using GoalDrivenAI.Navigation;
using NUnit.Framework;

namespace GoalDrivenAI.Tests
{
    public sealed class NavigationTests
    {
        private static CatalogHandle Location(string id, float x) => new CatalogHandle(id, HandleKind.Location, new Position(x, 0, 0));

        [Test]
        public void RegistryCapturesArbitraryLocationNamesAndRejectsActorsAndDuplicates()
        {
            var dock = Location("dock", 0);
            var source = new List<CatalogHandle> { dock, Location("market", 10) };
            var registry = new LocationRegistry(source); source.Clear();
            Assert.That(registry.Count, Is.EqualTo(2));
            Assert.That(registry["dock"], Is.SameAs(dock));
            Assert.That(registry.TryGetValue("missing", out _), Is.False);
            Assert.Throws<KeyNotFoundException>(() => { var missing = registry["missing"]; });
            Assert.Throws<ArgumentException>(() => new LocationRegistry(new[] { dock, dock }));
            Assert.Throws<ArgumentException>(() => new LocationRegistry(new[] {
                new CatalogHandle("actor", HandleKind.Actor, new Position(0, 0, 0)) }));
        }

    }
}
