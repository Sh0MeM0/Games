using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Profiles.Core;

namespace Profiles.AutoCAD
{
    /// <summary>
    /// Native, editable hatching without additional boundary database objects.
    /// User-defined parallel lines keep physical spacing independent of acad.pat,
    /// acadiso.pat, MEASUREMENT and the user's current hatch settings.
    /// </summary>
    internal static class DrawingHatch
    {
        internal static void Complete(Hatch hatch, DrawHatch region, Matrix3d transform)
        {
            var boundary = Boundary(region);
            if (!Finite(region.PatternSpacing) || region.PatternSpacing <= 0 || !Finite(region.PatternAngleDegrees))
                throw new ProfileValidationException("Некорректный шаг или угол штриховки площади профиля.");

            double angle = ((region.PatternAngleDegrees % 360 + 360) % 360) * Math.PI / 180;
            double minimum = double.PositiveInfinity, maximum = double.NegativeInfinity;
            var origin = boundary[0];
            foreach (var point in boundary)
            {
                double normalDistance = -(point.X - origin.X) * Math.Sin(angle) + (point.Y - origin.Y) * Math.Cos(angle);
                minimum = Math.Min(minimum, normalDistance);
                maximum = Math.Max(maximum, normalDistance);
            }
            // Use the same bounded density as preview/export, below AutoCAD's
            // own 100,000-line evaluation limit.
            if ((maximum - minimum) / region.PatternSpacing > HatchPatternGeometry.MaximumPatternLines)
                throw new ProfileValidationException("Штриховка площади слишком плотная для отображения.");

            try
            {
                // Hatch loops use the hatch's OCS, not WCS. Establish the final
                // plane first, then convert each local profile vertex through
                // the saved insertion/UCS transform into that plane's OCS.
                var normal = Vector3d.ZAxis.TransformBy(transform).GetNormal();
                var worldToPlane = Matrix3d.WorldToPlane(normal);
                var planeOrigin = Point3d.Origin.TransformBy(transform).TransformBy(worldToPlane);
                var direction = new Vector3d(Math.Cos(angle), Math.Sin(angle), 0)
                    .TransformBy(transform).TransformBy(worldToPlane);
                double planeAngle = Math.Atan2(direction.Y, direction.X);
                if (planeAngle < 0) planeAngle += 2 * Math.PI;
                hatch.Normal = normal;
                hatch.Elevation = planeOrigin.Z;
                hatch.Associative = false;
                hatch.HatchStyle = HatchStyle.Normal;
                hatch.SetHatchPattern(HatchPatternType.UserDefined, "_USER");
                hatch.PatternAngle = planeAngle;
                hatch.PatternSpace = region.PatternSpacing;
                hatch.PatternDouble = false;
                // Refresh the native pattern definition after changing its angle
                // and spacing; this never changes drawing-wide hatch variables.
                hatch.SetHatchPattern(HatchPatternType.UserDefined, "_USER");
                // Set the final origin after refreshing the pattern. All regions
                // share profile-local (0,0), identical to preview/export clipping.
                hatch.Origin = new Point2d(planeOrigin.X, planeOrigin.Y);
                var placedBoundary = new List<Point2>(boundary.Count);
                foreach (var point in boundary)
                {
                    var planePoint = new Point3d(point.X, point.Y, 0).TransformBy(transform).TransformBy(worldToPlane);
                    placedBoundary.Add(new Point2(planePoint.X, planePoint.Y));
                }
                // The old raw-vertex overload left closure dependent on the
                // native polyline convention. Supply every edge explicitly:
                // the final surface vertex MUST connect back to the daylight
                // vertex. Omitting it removes a large wedge, regardless of pitch.
                var curves = new Curve2dCollection();
                var edgeTypes = new IntegerCollection();
                try
                {
                    foreach (var edge in HatchBoundaryGeometry.ClosedEdges(placedBoundary))
                    {
                        curves.Add(new LineSegment2d(new Point2d(edge.Start.X, edge.Start.Y), new Point2d(edge.End.X, edge.End.Y)));
                        edgeTypes.Add(1); // ObjectARX AcDbHatch::kLine.
                    }
                    hatch.AppendLoop(HatchLoopTypes.External, curves, edgeTypes);
                }
                finally
                {
                    // appendLoop copies geometry into the database hatch.
                    foreach (Curve2d curve in curves) curve.Dispose();
                }
                hatch.EvaluateHatch(true);
                if (hatch.NumberOfLoops != 1)
                    throw new ProfileValidationException("AutoCAD не сохранил замкнутую границу штриховки. Прежнее построение сохранено.");
                // Do not read Hatch.Area during creation: some AutoCAD versions
                // populate that cache only after transaction/command completion.
            }
            catch (Autodesk.AutoCAD.Runtime.Exception exception)
            {
                // The caller aborts its one transaction; an existing profile and
                // its ownership metadata are left intact on any native failure.
                throw new ProfileValidationException("AutoCAD не смог построить штриховку площади: " +
                    exception.ErrorStatus + ". Прежнее построение сохранено.", exception);
            }
        }

        private static List<Point2> Boundary(DrawHatch region)
        {
            if (region.Boundary == null || region.Boundary.Count > 250000)
                throw new ProfileValidationException("Некорректная граница штриховки площади.");
            var result = new List<Point2>();
            foreach (var point in region.Boundary)
            {
                if (!Finite(point.X) || !Finite(point.Y) || Math.Abs(point.X) > 1e12 || Math.Abs(point.Y) > 1e12)
                    throw new ProfileValidationException("Некорректные координаты границы штриховки площади.");
                if (result.Count == 0 || !SamePoint(result[result.Count - 1], point)) result.Add(point);
            }
            if (result.Count > 1 && SamePoint(result[0], result[result.Count - 1])) result.RemoveAt(result.Count - 1);
            if (result.Count < 3) throw new ProfileValidationException("У границы штриховки площади менее трёх различных вершин.");

            // Translate before taking cross products to avoid cancellation when
            // a small valid polygon is far from the profile's local origin.
            var origin = result[0];
            double twiceArea = 0;
            for (int i = 1; i + 1 < result.Count; i++)
                twiceArea += (result[i].X - origin.X) * (result[i + 1].Y - origin.Y) -
                    (result[i + 1].X - origin.X) * (result[i].Y - origin.Y);
            if (!Finite(twiceArea) || twiceArea == 0)
                throw new ProfileValidationException("Граница штриховки имеет нулевую или некорректную площадь.");
            if (twiceArea < 0) result.Reverse();
            return result;
        }

        private static bool SamePoint(Point2 a, Point2 b) => a.X == b.X && a.Y == b.Y;
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
