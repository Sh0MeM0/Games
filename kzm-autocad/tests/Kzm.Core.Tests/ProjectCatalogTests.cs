using System.Collections.Generic;
using System.Linq;
using Kzm.Core;
using Xunit;

namespace Kzm.Core.Tests
{
    /// <summary>Замечание 35: список проектов всех чертежей, чтобы перенести проект в новый чертёж.</summary>
    public class ProjectCatalogTests
    {
        private static CatalogEntry Project(string id, string name, int surfaces = 2, int cartograms = 1) =>
            new CatalogEntry { ProjectId = id, Name = name, Surfaces = surfaces, Cartograms = cartograms };

        [Fact]
        public void RoundTrip()
        {
            var entries = new List<CatalogEntry>
            {
                new CatalogEntry { ProjectId = "a1", Name = "Чаусы\tдля примера", Path = @"D:\Проекты\Чаусы.dwg", Seen = "2026-11-20 10:15", Surfaces = 2, Cartograms = 1 },
            };
            var back = ProjectCatalog.Parse(ProjectCatalog.Format(entries));
            var e = Assert.Single(back);
            Assert.Equal("a1", e.ProjectId);
            Assert.Equal("Чаусы для примера", e.Name);
            Assert.Equal(@"D:\Проекты\Чаусы.dwg", e.Path);
            Assert.Equal("2026-11-20 10:15", e.Seen);
            Assert.Equal(2, e.Surfaces);
            Assert.Equal(1, e.Cartograms);
        }

        [Fact]
        public void UpdateReplacesOnlyThatDrawing()
        {
            var entries = new List<CatalogEntry>();
            Assert.True(ProjectCatalog.Update(entries, @"D:\a.dwg", new[] { Project("p1", "Площадка"), Project("p2", "Вариант 2") }, "2026-11-20 10:00"));
            Assert.True(ProjectCatalog.Update(entries, @"D:\b.dwg", new[] { Project("p3", "Двор") }, "2026-11-21 09:00"));
            // The same drawing again, nothing changed but the date: no rewrite of the file.
            Assert.False(ProjectCatalog.Update(entries, @"d:/A.dwg", new[] { Project("p1", "Площадка"), Project("p2", "Вариант 2") }, "2026-11-22 08:00"));
            // A project deleted in a.dwg and one renamed.
            Assert.True(ProjectCatalog.Update(entries, @"D:\a.dwg", new[] { Project("p1", "Площадка 1") }, "2026-11-22 08:00"));
            Assert.Equal(new[] { "p3", "p1" }, entries.Select(e => e.ProjectId).ToArray());
            Assert.Equal("Площадка 1", entries.Single(e => e.ProjectId == "p1").Name);
        }

        [Fact]
        public void OthersExcludeTheCurrentDrawingNewestFirst()
        {
            var entries = new List<CatalogEntry>();
            ProjectCatalog.Update(entries, @"D:\a.dwg", new[] { Project("p1", "Старый") }, "2026-10-01 10:00");
            ProjectCatalog.Update(entries, @"D:\b.dwg", new[] { Project("p2", "Новый") }, "2026-11-20 10:00");
            ProjectCatalog.Update(entries, @"D:\c.dwg", new[] { Project("p3", "Этот") }, "2026-11-21 10:00");
            Assert.Equal(new[] { "p2", "p1" }, ProjectCatalog.Others(entries, @"D:\C.DWG").Select(e => e.ProjectId).ToArray());
            // A new drawing (no file yet) sees all of them.
            Assert.Equal(3, ProjectCatalog.Others(entries, null).Count);
        }

        [Fact]
        public void BrokenLinesAreSkipped()
        {
            var entries = ProjectCatalog.Parse("# comment\nonly-id\n\tname\tD:\\x.dwg\np1\tИмя\tD:\\x.dwg\tдата\tне число\n");
            var e = Assert.Single(entries);
            Assert.Equal(0, e.Surfaces);
        }
    }
}
