using System;
using System.Collections.Generic;
using System.Linq;

namespace Profiles.Core
{
    internal static class SectionGeometry
    {
        internal static List<Point2> Trapezoid(CrossSection section)
        {
            double bottom = section.ProjectBottom.Value;
            double left = section.CenterOffset - section.BottomWidth.Value / 2;
            double right = section.CenterOffset + section.BottomWidth.Value / 2;
            Point2? leftHit = Intersect(section.Points, new Point2(left, bottom), -section.LeftSlope.Value);
            Point2? rightHit = Intersect(section.Points, new Point2(right, bottom), section.RightSlope.Value);
            if (!leftHit.HasValue || !rightHit.HasValue)
                ProfileValidation.Fail("Поперечник «" + section.Name + "»: " + (!leftHit.HasValue ? "левый" : "правый") + " откос не пересекает измеренную поверхность. Продлите поперечник, заполните пропуски натурных отметок или измените параметры трапеции.");
            return new List<Point2> { leftHit.Value, new Point2(left, bottom), new Point2(right, bottom), rightHit.Value };
        }

        // The ray is origin + t*(horizontalRatio, 1), t >= 0; all data are metres.
        private static Point2? Intersect(List<SectionPoint> points, Point2 origin, double horizontalRatio)
        {
            Point2? nearest = null;
            double nearestT = double.PositiveInfinity;
            for (int i = 1; i < points.Count; i++)
            {
                SectionPoint a = points[i - 1], b = points[i];
                if (!a.Existing.HasValue || !b.Existing.HasValue) continue;
                double dx = b.Offset - a.Offset;
                double dz = b.Existing.Value - a.Existing.Value;
                double determinant = horizontalRatio * dz - dx;
                double ax = a.Offset - origin.X, az = a.Existing.Value - origin.Y;
                if (Math.Abs(determinant) < 1e-12)
                {
                    // Collinear terrain may begin at the ray origin or farther outward.
                    if (Math.Abs(ax - horizontalRatio * az) < 1e-9)
                    {
                        double minT = Math.Min(az, b.Existing.Value - origin.Y);
                        double maxT = Math.Max(az, b.Existing.Value - origin.Y);
                        if (maxT >= 0 && Math.Max(0, minT) < nearestT)
                        {
                            nearestT = Math.Max(0, minT);
                            nearest = new Point2(origin.X + horizontalRatio * nearestT, origin.Y + nearestT);
                        }
                    }
                    continue;
                }
                double t = (ax * dz - az * dx) / determinant;
                double u = (ax - horizontalRatio * az) / determinant;
                if (t >= -1e-9 && u >= -1e-9 && u <= 1 + 1e-9 && t < nearestT)
                {
                    nearestT = Math.Max(0, t);
                    nearest = new Point2(origin.X + horizontalRatio * nearestT, origin.Y + nearestT);
                }
            }
            return nearest;
        }

        internal static List<List<Point2>> Runs(IEnumerable<SectionPoint> points, bool design)
        {
            var runs = new List<List<Point2>>();
            var current = new List<Point2>();
            foreach (SectionPoint point in points)
            {
                double? elevation = design ? point.Design : point.Existing;
                if (elevation.HasValue) current.Add(new Point2(point.Offset, elevation.Value));
                else if (current.Count > 0) { runs.Add(current); current = new List<Point2>(); }
            }
            if (current.Count > 0) runs.Add(current);
            return runs;
        }

        internal static List<List<Point2>> Water(List<List<Point2>> boundary, double level)
        {
            var result = new List<List<Point2>>();
            foreach (List<Point2> run in boundary)
            {
                List<Point2> interval = null;
                for (int i = 1; i < run.Count; i++)
                {
                    Point2 a = run[i - 1], b = run[i];
                    if (a.Y >= level && b.Y >= level) { interval = null; continue; }
                    double start = a.X, end = b.X;
                    if (a.Y > level) start = a.X + (level - a.Y) * (b.X - a.X) / (b.Y - a.Y);
                    if (b.Y > level) end = a.X + (level - a.Y) * (b.X - a.X) / (b.Y - a.Y);
                    if (end - start <= 1e-10) { interval = null; continue; }
                    if (interval != null && Math.Abs(interval[1].X - start) <= 1e-9)
                        interval[1] = new Point2(end, level);
                    else
                    {
                        interval = new List<Point2> { new Point2(start, level), new Point2(end, level) };
                        result.Add(interval);
                    }
                }
            }
            return result;
        }

        internal static double? Interpolate(List<Point2> curve, double x)
        {
            for (int i = 1; i < curve.Count; i++)
                if (x >= curve[i - 1].X - 1e-9 && x <= curve[i].X + 1e-9)
                {
                    Point2 a = curve[i - 1], b = curve[i];
                    if (b.X - a.X < 1e-12) return a.Y;
                    return a.Y + (x - a.X) * (b.Y - a.Y) / (b.X - a.X);
                }
            return null;
        }
    }
}
