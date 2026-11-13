using System;
using System.Collections.Generic;
using System.Linq;

namespace Kzm.Core
{
    public sealed class ControlVolumeResult
    {
        /// <summary>Насыпь, м³ (точно для поверхностей TIN).</summary>
        public double Fill { get; internal set; }
        /// <summary>Выемка, м³ (положительное число).</summary>
        public double Cut { get; internal set; }
        /// <summary>Площадь внутри границы, где есть обе поверхности, м².</summary>
        public double Area { get; internal set; }
        /// <summary>Площадь внутри границы без данных хотя бы одной поверхности, м².</summary>
        public double MissingArea { get; internal set; }

        /// <summary>Расхождение метода квадратов с контрольным расчётом: м³ и % от контрольного объёма.</summary>
        public static (double Cubic, double? Percent) Difference(double squares, double control)
        {
            double cubic = squares - control;
            double? percent = Math.Abs(control) > 1e-9 ? cubic / control * 100 : (double?)null;
            return (cubic, percent);
        }
    }

    /// <summary>
    /// Контрольный расчёт (задание п. 5): объём между поверхностями напрямую по наложению
    /// триангуляций. Граница работ, треугольники фактической и проектной поверхностей и их
    /// внешние границы пересекаются; на каждом кусочке рабочая отметка линейна, поэтому
    /// V = S·(h1+h2+h3)/3 по частям насыпи и выемки даёт точный объём между TIN.
    /// </summary>
    public static class ControlVolume
    {
        private const double TinyArea = 1e-10;

        /// <summary>Several parts of the boundary of works (замечание 31): the sum over the parts (they do not overlap).</summary>
        public static ControlVolumeResult Compute(IReadOnlyList<IReadOnlyList<XY>> boundaries, Tin fact, Tin project)
        {
            if (boundaries == null || boundaries.Count == 0) throw new KzmException("Граница не задана.");
            var total = new ControlVolumeResult();
            foreach (var boundary in boundaries)
            {
                var part = Compute(boundary, fact, project);
                total.Fill += part.Fill;
                total.Cut += part.Cut;
                total.Area += part.Area;
                total.MissingArea += part.MissingArea;
            }
            return total;
        }

        public static ControlVolumeResult Compute(IReadOnlyList<XY> boundary, Tin fact, Tin project)
        {
            if (boundary == null || boundary.Count < 3) throw new KzmException("Граница не задана.");
            if (fact == null || project == null) throw new ArgumentNullException(fact == null ? nameof(fact) : nameof(project));
            var outline = Geometry.Normalize(boundary);
            if (outline.Count < 3) throw new KzmException("Граница вырождена.");
            // Local frame: areas of survey coordinates (~5 000 000 m) lose precision otherwise.
            var origin = outline[0];
            XY L(XY p) => new XY(p.X - origin.X, p.Y - origin.Y);
            XY W(XY p) => new XY(p.X + origin.X, p.Y + origin.Y);
            var region = Windows(outline.Select(L).ToList(), "границы работ");
            var factMask = fact.Boundaries.Count > 0
                ? fact.Boundaries.SelectMany(part => Windows(part.Select(L).ToList(), "границы фактической поверхности")).ToList() : null;
            var projectMask = project.Boundaries.Count > 0
                ? project.Boundaries.SelectMany(part => Windows(part.Select(L).ToList(), "границы проектной поверхности")).ToList() : null;
            var result = new ControlVolumeResult();
            double regionArea = Geometry.Area(outline.Select(L).ToList());
            var box = Box(outline);

            foreach (int f in fact.TrianglesInBox(box.MinX, box.MinY, box.MaxX, box.MaxY).ToList())
            {
                var (fa, fb, fc) = fact.Triangle(f);
                var factTriangle = new[] { L(fact.Point(fa)), L(fact.Point(fb)), L(fact.Point(fc)) };
                var factBox = Box(factTriangle);
                foreach (var window in region)
                {
                    if (!Overlaps(factBox, window.Box)) continue;
                    var piece = Geometry.ClipByConvex(factTriangle, window.Points);
                    foreach (var masked in Mask(piece, factMask))
                    {
                        var maskedBox = Box(masked);
                        var world = Box(masked.Select(W).ToList());
                        foreach (int q in project.TrianglesInBox(world.MinX, world.MinY, world.MaxX, world.MaxY))
                        {
                            var (qa, qb, qc) = project.Triangle(q);
                            var projectTriangle = new[] { L(project.Point(qa)), L(project.Point(qb)), L(project.Point(qc)) };
                            if (!Overlaps(maskedBox, Box(projectTriangle))) continue;
                            var overlap = Geometry.ClipByConvex(masked, projectTriangle);
                            foreach (var final in Mask(overlap, projectMask))
                            {
                                double area = Geometry.Area(final);
                                if (area <= TinyArea) continue;
                                var h = final.Select(p => project.ZInTriangle(q, W(p)) - fact.ZInTriangle(f, W(p))).ToArray();
                                result.Area += area;
                                Integrate(final, h, result);
                            }
                        }
                    }
                }
            }
            result.MissingArea = Math.Max(0, regionArea - result.Area);
            if (result.MissingArea <= 1e-9 * Math.Max(1, regionArea)) result.MissingArea = 0;
            return result;
        }

        private sealed class Window
        {
            internal XY[] Points;
            internal (double MinX, double MinY, double MaxX, double MaxY) Box;
        }

        /// <summary>Convex pieces (ear clipping) of a simple polygon.</summary>
        private static List<Window> Windows(List<XY> polygon, string what)
        {
            var normalized = Geometry.Normalize(polygon);
            var triangles = Geometry.Triangulate(normalized);
            if (triangles == null || triangles.Count == 0)
                throw new KzmException("контрольный расчёт: контур " + what + " сложной формы (самокасания) — не разбивается на треугольники.");
            return triangles.Select(t => new Window { Points = t, Box = Box(t) }).ToList();
        }

        /// <summary>Parts of a convex polygon inside the mask (all of it when there is no mask).</summary>
        private static IEnumerable<List<XY>> Mask(List<XY> polygon, List<Window> mask)
        {
            if (polygon.Count < 3 || Geometry.Area(polygon) <= TinyArea) yield break;
            if (mask == null)
            {
                yield return polygon;
                yield break;
            }
            var box = Box(polygon);
            foreach (var window in mask)
            {
                if (!Overlaps(box, window.Box)) continue;
                var part = Geometry.ClipByConvex(polygon, window.Points);
                if (part.Count >= 3 && Geometry.Area(part) > TinyArea) yield return part;
            }
        }

        private static (double MinX, double MinY, double MaxX, double MaxY) Box(IReadOnlyList<XY> points) =>
            (points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y));

        private static bool Overlaps((double MinX, double MinY, double MaxX, double MaxY) a, (double MinX, double MinY, double MaxX, double MaxY) b) =>
            a.MinX <= b.MaxX + 1e-9 && b.MinX <= a.MaxX + 1e-9 && a.MinY <= b.MaxY + 1e-9 && b.MinY <= a.MaxY + 1e-9;

        /// <summary>Convex polygon, linear h at its vertices: fill and cut by the formula of п. 5 over the parts of each fan triangle.</summary>
        private static void Integrate(List<XY> polygon, double[] h, ControlVolumeResult result)
        {
            for (int k = 1; k + 1 < polygon.Count; k++)
            {
                var (fill, cut) = Split(new[] { polygon[0], polygon[k], polygon[k + 1] }, new[] { h[0], h[k], h[k + 1] });
                result.Fill += fill;
                result.Cut += cut;
            }
        }

        internal static (double Fill, double Cut) Split(XY[] p, double[] h)
        {
            var positive = new List<(XY P, double H)>();
            var negative = new List<(XY P, double H)>();
            for (int i = 0; i < 3; i++)
            {
                int j = (i + 1) % 3;
                if (h[i] >= 0) positive.Add((p[i], h[i]));
                if (h[i] <= 0) negative.Add((p[i], h[i]));
                if ((h[i] > 0 && h[j] < 0) || (h[i] < 0 && h[j] > 0))
                {
                    var z = Geometry.Lerp(p[i], p[j], h[i] / (h[i] - h[j]));
                    positive.Add((z, 0));
                    negative.Add((z, 0));
                }
            }
            return (Math.Max(0, Volume(positive)), Math.Max(0, -Volume(negative)));
        }

        private static double Volume(List<(XY P, double H)> polygon)
        {
            double sum = 0;
            for (int k = 1; k + 1 < polygon.Count; k++)
            {
                double area = Math.Abs(Geometry.Orient(polygon[0].P, polygon[k].P, polygon[k + 1].P)) / 2;
                sum += area * (polygon[0].H + polygon[k].H + polygon[k + 1].H) / 3;
            }
            return sum;
        }
    }
}
