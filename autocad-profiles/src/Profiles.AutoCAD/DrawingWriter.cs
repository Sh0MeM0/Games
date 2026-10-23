using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Profiles.Core;
using TextAlignment = Profiles.Core.TextAlignment;

namespace Profiles.AutoCAD
{
    internal sealed class InsertResult
    {
        public int EntityCount { get; set; }
        public List<string> Warnings { get; } = new List<string>();
    }

    internal static class DrawingWriter
    {
        internal static InsertResult Insert(Database database, Editor editor,
            Point3d insertionPoint, DrawingPlan plan)
        {
            var result = new InsertResult();
            var ucsMatrix = editor.CurrentUserCoordinateSystem;
            var ucs = ucsMatrix.CoordinateSystem3d;
            // Editor.GetPoint returns UCS coordinates, as demonstrated by Autodesk
            // ADN-DevTech/MgdDbg ObjTests/MakeEntTests.cs Point(). Convert the
            // insertion to WCS and orient the local plan along the current UCS.
            var insertionWcs = insertionPoint.TransformBy(ucsMatrix);
            var transform = Matrix3d.AlignCoordinateSystem(Point3d.Origin,
                Vector3d.XAxis, Vector3d.YAxis, Vector3d.ZAxis,
                insertionWcs, ucs.Xaxis, ucs.Yaxis, ucs.Zaxis);
            var dashScale = plan.Entities.OfType<DrawText>()
                .Where(text => text.Height > 0).Select(text => text.Height)
                .DefaultIfEmpty(1.0).Min();

            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var space = (BlockTableRecord)transaction.GetObject(
                    database.CurrentSpaceId, OpenMode.ForWrite);
                var layers = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
                var layerIds = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
                var dashedId = plan.Entities.Any(entity => entity.Dashed)
                    ? DashedLinetype(database, transaction) : ObjectId.Null;

                foreach (var item in plan.Entities)
                {
                    if (string.IsNullOrWhiteSpace(item.Layer) || !item.Layer.StartsWith("PROF_", StringComparison.Ordinal))
                        throw new InvalidOperationException("Некорректный слой профиля.");
                    if (!layerIds.TryGetValue(item.Layer, out var layerId))
                    {
                        layerId = GetLayer(item.Layer, layers, transaction, result.Warnings);
                        layerIds.Add(item.Layer, layerId);
                    }

                    var entity = CreateEntity(item, database);
                    if (entity == null) continue;
                    entity.LayerId = layerId;
                    entity.Color = Color.FromColorIndex(ColorMethod.ByAci, item.ColorIndex);
                    entity.LineWeight = GetLineWeight(item.LineWeightMm);
                    if (item.Dashed)
                    {
                        entity.LinetypeId = dashedId;
                        entity.LinetypeScale = dashScale;
                    }
                    entity.TransformBy(transform);
                    space.AppendEntity(entity);
                    transaction.AddNewlyCreatedDBObject(entity, true);
                    if (entity is DBText text) text.AdjustAlignment(database);
                    result.EntityCount++;
                }
                transaction.Commit();
            }
            return result;
        }

        private static Entity CreateEntity(DrawEntity item, Database database)
        {
            if (item is DrawPolyline line)
            {
                if (line.Points.Count < 2) return null;
                var polyline = new Polyline(line.Points.Count);
                polyline.SetDatabaseDefaults(database);
                for (var i = 0; i < line.Points.Count; i++)
                    polyline.AddVertexAt(i, new Point2d(line.Points[i].X, line.Points[i].Y), 0, 0, 0);
                polyline.Closed = line.Closed;
                return polyline;
            }
            if (item is DrawCircle circle)
            {
                var entity = new Circle();
                entity.SetDatabaseDefaults(database);
                entity.Center = new Point3d(circle.Center.X, circle.Center.Y, 0);
                entity.Normal = Vector3d.ZAxis;
                entity.Radius = circle.Radius;
                return entity;
            }
            if (item is DrawText label)
            {
                var position = new Point3d(label.Position.X, label.Position.Y, 0);
                var value = label.Text ?? string.Empty;
                if (value.Contains("\n") || label.MaxWidth > 0)
                {
                    var multiline = new MText();
                    multiline.SetDatabaseDefaults(database);
                    multiline.TextStyleId = database.Textstyle;
                    multiline.TextHeight = label.Height;
                    multiline.Width = Math.Max(0, label.MaxWidth);
                    multiline.Rotation = label.RotationDegrees * Math.PI / 180;
                    multiline.Attachment = label.Alignment == TextAlignment.Center ? AttachmentPoint.MiddleCenter :
                        label.Alignment == TextAlignment.Right ? AttachmentPoint.MiddleRight : AttachmentPoint.MiddleLeft;
                    multiline.Location = position;
                    var paragraphAlignment = label.Alignment == TextAlignment.Center ? "c" :
                        label.Alignment == TextAlignment.Right ? "r" : "l";
                    multiline.Contents = "{\\W" + label.WidthFactor.ToString("0.########", CultureInfo.InvariantCulture) +
                        ";\\pxq" + paragraphAlignment + ";" + EscapeMText(value) + "}";
                    return multiline;
                }
                var text = new DBText();
                text.SetDatabaseDefaults(database);
                text.Position = position;
                text.TextString = value;
                text.TextStyleId = database.Textstyle;
                text.Height = label.Height;
                text.WidthFactor = label.WidthFactor;
                text.Rotation = label.RotationDegrees * Math.PI / 180;
                text.HorizontalMode = label.Alignment == TextAlignment.Center ? TextHorizontalMode.TextCenter :
                    label.Alignment == TextAlignment.Right ? TextHorizontalMode.TextRight : TextHorizontalMode.TextLeft;
                text.VerticalMode = TextVerticalMode.TextVerticalMid;
                text.AlignmentPoint = position;
                return text;
            }
            throw new InvalidOperationException("Неизвестный элемент профиля.");
        }

        private static string EscapeMText(string value) => value.Replace("\\", "\\\\")
            .Replace("{", "\\{").Replace("}", "\\}").Replace("\r\n", "\n")
            .Replace("\r", "\n").Replace("\n", "\\P");

        private static ObjectId GetLayer(string requested, LayerTable layers,
            Transaction transaction, List<string> warnings)
        {
            var name = requested;
            for (var suffix = 2; layers.Has(name); suffix++)
            {
                var existing = (LayerTableRecord)transaction.GetObject(layers[name], OpenMode.ForRead);
                if (!existing.IsLocked && !existing.IsOff && !existing.IsFrozen)
                {
                    if (name != requested)
                        warnings.Add("Слой " + requested + " выключен, заморожен или заблокирован; использован " + name + ".");
                    return existing.ObjectId;
                }
                name = requested + "_" + suffix.ToString(CultureInfo.InvariantCulture);
            }
            if (!layers.IsWriteEnabled) layers.UpgradeOpen();
            var layer = new LayerTableRecord { Name = name };
            var id = layers.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
            if (name != requested)
                warnings.Add("Слой " + requested + " выключен, заморожен или заблокирован; создан " + name + ".");
            return id;
        }

        private static ObjectId DashedLinetype(Database database, Transaction transaction)
        {
            var table = (LinetypeTable)transaction.GetObject(database.LinetypeTableId, OpenMode.ForRead);
            var name = "PROF_DASHED";
            for (var suffix = 2; table.Has(name); suffix++)
            {
                var existing = (LinetypeTableRecord)transaction.GetObject(table[name], OpenMode.ForRead);
                if (existing.NumDashes == 2 && Math.Abs(existing.PatternLength - 2) < 1e-9 &&
                    Math.Abs(existing.DashLengthAt(0) - 1.2) < 1e-9 &&
                    Math.Abs(existing.DashLengthAt(1) + 0.8) < 1e-9)
                    return existing.ObjectId;
                name = "PROF_DASHED_" + suffix.ToString(CultureInfo.InvariantCulture);
            }
            table.UpgradeOpen();
            var pattern = new LinetypeTableRecord
            {
                Name = name,
                AsciiDescription = "Профиля: штриховая линия",
                PatternLength = 2,
                NumDashes = 2
            };
            pattern.SetDashLengthAt(0, 1.2);
            pattern.SetDashLengthAt(1, -0.8);
            var id = table.Add(pattern);
            transaction.AddNewlyCreatedDBObject(pattern, true);
            return id;
        }

        private static LineWeight GetLineWeight(double millimetres)
        {
            var weights = new[] { 0, 5, 9, 13, 15, 18, 20, 25, 30, 35, 40, 50, 53, 60, 70, 80, 90, 100, 106, 120, 140, 158, 200, 211 };
            var desired = Math.Max(0, millimetres * 100);
            return (LineWeight)weights.OrderBy(weight => Math.Abs(weight - desired)).First();
        }
    }
}
