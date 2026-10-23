using System;
using System.Collections.Generic;

namespace Profiles.Core
{
    /// <summary>Explicit closed hatch edges, independent of a CAD API's polyline closing convention.</summary>
    public static class HatchBoundaryGeometry
    {
        public static List<HatchStroke> ClosedEdges(IList<Point2> boundary)
        {
            if (boundary == null || boundary.Count < 3 || boundary.Count > 250000)
                throw new ProfileValidationException("Некорректная граница штриховки площади.");
            var vertices = new List<Point2>(boundary.Count);
            foreach (var point in boundary)
            {
                if (!Finite(point.X) || !Finite(point.Y) || Math.Abs(point.X) > 1e12 || Math.Abs(point.Y) > 1e12)
                    throw new ProfileValidationException("Некорректные координаты границы штриховки площади.");
                if (vertices.Count == 0 || !Same(vertices[vertices.Count - 1], point)) vertices.Add(point);
            }
            if (vertices.Count > 1 && Same(vertices[0], vertices[vertices.Count - 1])) vertices.RemoveAt(vertices.Count - 1);
            if (vertices.Count < 3 || Area(vertices) == 0)
                throw new ProfileValidationException("Граница штриховки имеет нулевую площадь.");
            var edges = new List<HatchStroke>(vertices.Count);
            for (int index = 0; index < vertices.Count; index++)
                edges.Add(new HatchStroke { Start = vertices[index], End = vertices[(index + 1) % vertices.Count] });
            return edges;
        }

        public static double Area(IList<Point2> boundary)
        {
            if (boundary == null || boundary.Count < 3) return 0;
            var origin = boundary[0]; double sum = 0, compensation = 0;
            for (int index = 1; index + 1 < boundary.Count; index++)
            {
                double term = (boundary[index].X - origin.X) * (boundary[index + 1].Y - origin.Y) -
                    (boundary[index + 1].X - origin.X) * (boundary[index].Y - origin.Y);
                double corrected = term - compensation, total = sum + corrected;
                compensation = (total - sum) - corrected;
                sum = total;
            }
            return Math.Abs(sum) / 2;
        }

        private static bool Same(Point2 a, Point2 b) => a.X == b.X && a.Y == b.Y;
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
