using System;
using System.Collections.Generic;
using System.Linq;
using Kzm.Core;
using Xunit;

namespace Kzm.Core.Tests
{
    public class NearestIndexTests
    {
        public static IEnumerable<object[]> Layouts() => new[]
        {
            new object[] { "scattered", 0 }, new object[] { "line", 1 }, new object[] { "clusters", 2 }, new object[] { "single", 3 },
        };

        [Theory]
        [MemberData(nameof(Layouts))]
        public void TwoNearestEqualsBruteForce(string name, int layout)
        {
            var random = new Random(12345 + layout);
            var points = new List<XY>();
            int count = layout == 3 ? 1 : 400;
            for (int i = 0; i < count; i++)
            {
                switch (layout)
                {
                    case 0: points.Add(new XY(5_500_000 + random.NextDouble() * 500, 4_300_000 + random.NextDouble() * 300)); break;
                    case 1: points.Add(new XY(5_500_000 + random.NextDouble() * 500, 4_300_000)); break;
                    default:
                        double cx = random.Next(3) * 1000, cy = random.Next(2) * 700;
                        points.Add(new XY(5_500_000 + cx + random.NextDouble() * 5, 4_300_000 + cy + random.NextDouble() * 5));
                        break;
                }
            }
            var index = new NearestIndex(points);
            for (int q = 0; q < 300; q++)
            {
                // Queries inside and well outside the point cloud.
                var query = new XY(5_499_000 + random.NextDouble() * 3000, 4_299_000 + random.NextDouble() * 3000);
                var expected = points.Select((p, i) => (i, d: p.DistanceTo(query))).OrderBy(t => t.d).ThenBy(t => t.i).Take(2).ToList();
                var (first, d1, second, d2) = index.TwoNearest(query);
                Assert.Equal(expected[0].d, d1, 9);
                if (expected.Count > 1) Assert.Equal(expected[1].d, d2, 9);
                else Assert.Equal(-1, second);
                Assert.Equal(expected[0].d, points[first].DistanceTo(query), 9);
            }
        }
    }
}
