using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Kzm.Core;

namespace Kzm.AutoCAD.Drawing
{
    /// <summary>Reading survey texts and markers from the drawing.</summary>
    internal static class Sources
    {
        internal static SourceText ReadText(Entity entity)
        {
            string handle = entity.Handle.ToString();
            if (entity is MText mtext)
            {
                string contents = mtext.Contents ?? string.Empty;
                return new SourceText(handle, SourceKind.MText, contents, TextCleaner.CleanMText(contents), Plan(mtext.Location),
                    mtext.TextStyleName, mtext.TextHeight);
            }
            if (entity is DBText text)
            {
                string value = text.TextString ?? string.Empty;
                return new SourceText(handle, SourceKind.Text, value, TextCleaner.CleanText(value), Plan(TextAnchor(text)),
                    text.TextStyleName, text.Height);
            }
            return null;
        }

        /// <summary>
        /// The point AutoCAD shows as the insertion point: Position for left/aligned/fit
        /// texts, AlignmentPoint for all other justifications.
        /// </summary>
        internal static Point3d TextAnchor(DBText text)
        {
            bool leftBase = text.HorizontalMode == TextHorizontalMode.TextLeft && text.VerticalMode == TextVerticalMode.TextBase;
            bool twoPoint = text.HorizontalMode == TextHorizontalMode.TextAlign || text.HorizontalMode == TextHorizontalMode.TextFit;
            return leftBase || twoPoint ? text.Position : text.AlignmentPoint;
        }

        internal static XY Plan(Point3d point) => new XY(point.X, point.Y);

        internal static XY? MarkerPosition(Entity entity)
        {
            switch (entity)
            {
                case DBPoint point: return Plan(point.Position);
                case Circle circle: return Plan(circle.Center);
                case BlockReference block: return Plan(block.Position);
                default: return null;
            }
        }

        internal static string EffectiveName(BlockReference block, Transaction transaction)
        {
            var id = block.IsDynamicBlock ? block.DynamicBlockTableRecord : block.BlockTableRecord;
            return ((BlockTableRecord)transaction.GetObject(id, OpenMode.ForRead)).Name;
        }

        /// <summary>Entities of the given DXF types and layers in the current space.</summary>
        internal static List<ObjectId> SelectAllInCurrentSpace(Editor editor, Database database, string dxfTypes, string layers)
        {
            var filter = new List<TypedValue> { new TypedValue((int)DxfCode.Start, dxfTypes) };
            if (!string.IsNullOrEmpty(layers)) filter.Add(new TypedValue((int)DxfCode.LayerName, layers));
            var result = editor.SelectAll(new SelectionFilter(filter.ToArray()));
            if (result.Status != PromptStatus.OK || result.Value == null) return new List<ObjectId>();
            var space = database.CurrentSpaceId;
            using (var transaction = database.TransactionManager.StartOpenCloseTransaction())
            {
                return result.Value.GetObjectIds()
                    .Where(id => transaction.GetObject(id, OpenMode.ForRead) is Entity entity && entity.OwnerId == space)
                    .ToList();
            }
        }

        /// <summary>Wildcard list for a selection filter: commas separate names, special characters escaped.</summary>
        internal static string LayerFilter(IEnumerable<string> names) =>
            string.Join(",", names.Distinct(StringComparer.OrdinalIgnoreCase).Select(EscapeWildcards));

        internal static string EscapeWildcards(string name)
        {
            var output = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
            {
                if ("#@.*?~[]-,`".IndexOf(c) >= 0) output.Append('`');
                output.Append(c);
            }
            return output.ToString();
        }

        /// <summary>
        /// Outline of a closed curve: LWPOLYLINE (arcs approximated every 5°), 2D/3D polyline,
        /// circle or ellipse/spline sampled. Z is ignored.
        /// </summary>
        internal static List<XY> Outline(Entity entity, Transaction transaction)
        {
            var result = new List<XY>();
            switch (entity)
            {
                case Polyline polyline:
                {
                    if (!polyline.Closed && polyline.NumberOfVertices > 2 &&
                        polyline.GetPoint2dAt(0).GetDistanceTo(polyline.GetPoint2dAt(polyline.NumberOfVertices - 1)) > 1e-6)
                        throw new KzmException("Полилиния границы не замкнута.");
                    int n = polyline.NumberOfVertices;
                    for (int i = 0; i < n; i++)
                    {
                        var a = polyline.GetPoint2dAt(i);
                        result.Add(new XY(a.X, a.Y));
                        double bulge = polyline.GetBulgeAt(i);
                        if (Math.Abs(bulge) < 1e-9 || (!polyline.Closed && i == n - 1)) continue;
                        var b = polyline.GetPoint2dAt((i + 1) % n);
                        double angle = 4 * Math.Atan(bulge);
                        int steps = Math.Max(2, (int)Math.Ceiling(Math.Abs(angle) / (Math.PI / 36)));
                        double chord = a.GetDistanceTo(b);
                        if (chord < 1e-12) continue;
                        double radius = chord / (2 * Math.Sin(Math.Abs(angle) / 2));
                        double mx = (a.X + b.X) / 2, my = (a.Y + b.Y) / 2;
                        double d = Math.Sqrt(Math.Max(0, radius * radius - chord * chord / 4));
                        double nx = -(b.Y - a.Y) / chord, ny = (b.X - a.X) / chord;
                        double side = (bulge > 0) == (Math.Abs(angle) < Math.PI) ? 1 : -1;
                        double cx = mx + side * nx * d, cy = my + side * ny * d;
                        double start = Math.Atan2(a.Y - cy, a.X - cx);
                        for (int k = 1; k < steps; k++)
                        {
                            double t = start + angle * k / steps;
                            result.Add(new XY(cx + radius * Math.Cos(t), cy + radius * Math.Sin(t)));
                        }
                    }
                    break;
                }
                case Circle circle:
                    for (int k = 0; k < 72; k++)
                        result.Add(new XY(circle.Center.X + circle.Radius * Math.Cos(k * Math.PI / 36), circle.Center.Y + circle.Radius * Math.Sin(k * Math.PI / 36)));
                    break;
                case Curve curve when curve.Closed:
                {
                    double startParam = curve.StartParam, endParam = curve.EndParam;
                    int steps = curve is Polyline2d || curve is Polyline3d ? 0 : 200;
                    if (steps == 0)
                    {
                        foreach (ObjectId id in curve is Polyline2d p2 ? (System.Collections.IEnumerable)p2 : (Polyline3d)curve)
                        {
                            var vertex = (Entity)transaction.GetObject(id, OpenMode.ForRead);
                            var position = vertex is Vertex2d v2 ? v2.Position : ((PolylineVertex3d)vertex).Position;
                            result.Add(new XY(position.X, position.Y));
                        }
                    }
                    else
                    {
                        for (int k = 0; k < steps; k++)
                        {
                            var point = curve.GetPointAtParameter(startParam + (endParam - startParam) * k / steps);
                            result.Add(new XY(point.X, point.Y));
                        }
                    }
                    break;
                }
                default:
                    throw new KzmException("Граница должна быть замкнутой полилинией (или кругом, сплайном, эллипсом).");
            }
            var outline = Geometry.Normalize(result);
            if (outline.Count < 3) throw new KzmException("Граница вырождена: нулевая площадь.");
            return outline;
        }

        internal static bool SameHeight(double a, double b) => Math.Abs(a - b) <= 1e-9 * Math.Max(1.0, Math.Max(Math.Abs(a), Math.Abs(b)));
    }
}
