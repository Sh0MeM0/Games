using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Kzm.Core;

namespace Kzm.AutoCAD.Drawing
{
    internal static class Layers
    {
        /// <summary>Check marks: not plotted, red, removable with KZM_CLEARMARKS.</summary>
        internal const string Check = "КЗМ_ПРОВЕРКА";
        // Layers of the task, п. 8.
        internal const string Triangles = "КЗМ_ТРЕУГ";
        internal const string Grid = "КЗМ_СЕТКА";
        internal const string FactLabels = "КЗМ_ОТМ_ФАКТ";
        internal const string ProjectLabels = "КЗМ_ОТМ_ПРОЕКТ";
        internal const string WorkingLabels = "КЗМ_ОТМ_РАБ";
        internal const string ZeroLine = "КЗМ_НУЛ_ЛИНИЯ";
        internal const string Volumes = "КЗМ_ОБЪЕМЫ";
        internal const string Totals = "КЗМ_ИТОГИ";
        /// <summary>Hatching of cut areas (СТБ 2073-2010, п. 7.1 б); not in the list of п. 8 — a separate layer to switch it off.</summary>
        internal const string CutHatch = "КЗМ_ВЫЕМКА_ШТРИХ";

        /// <summary>Thin dashed line for the zero line (СТБ 2073-2010, п. 3.12); falls back to a solid line.</summary>
        internal static void EnsureDashed(Database database, Transaction transaction, string layerName)
        {
            Ensure(database, transaction, layerName);
            var types = (LinetypeTable)transaction.GetObject(database.LinetypeTableId, OpenMode.ForRead);
            string name = "DASHED";
            if (!types.Has(name))
            {
                foreach (var file in new[] { "acadiso.lin", "acad.lin" })
                {
                    try { database.LoadLineTypeFile(name, file); break; }
                    catch (Autodesk.AutoCAD.Runtime.Exception) { }
                }
            }
            types = (LinetypeTable)transaction.GetObject(database.LinetypeTableId, OpenMode.ForRead);
            var table = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
            var layer = (LayerTableRecord)transaction.GetObject(table[layerName], OpenMode.ForRead);
            if (types.Has(name) && layer.LinetypeObjectId != types[name])
            {
                layer.UpgradeOpen();
                layer.LinetypeObjectId = types[name];
                layer.LineWeight = LineWeight.LineWeight018;
            }
        }

        internal static void Ensure(Database database, Transaction transaction, string name, short colorIndex = 7, bool plottable = true)
        {
            SymbolUtilityServices.ValidateSymbolName(name, false);
            var table = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
            if (table.Has(name)) return;
            table.UpgradeOpen();
            var layer = new LayerTableRecord
            {
                Name = name,
                Color = Color.FromColorIndex(ColorMethod.ByAci, colorIndex),
                IsPlottable = plottable,
            };
            table.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
        }

        internal static bool IsUsable(Database database, Transaction transaction, string name, out string problem)
        {
            problem = null;
            var table = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
            if (!table.Has(name)) return true;
            var layer = (LayerTableRecord)transaction.GetObject(table[name], OpenMode.ForRead);
            if (layer.IsLocked) problem = "слой «" + name + "» заблокирован";
            else if (layer.IsFrozen) problem = "слой «" + name + "» заморожен";
            return problem == null;
        }
    }

    /// <summary>
    /// Persistent marks next to problem objects on layer КЗМ_ПРОВЕРКА: a circle and
    /// a short reason. Each mark carries XData «SH0MEM0_KZM_MARK» with the reason
    /// and the handle of the object it points to.
    /// </summary>
    internal static class CheckMarks
    {
        internal const string AppName = "SH0MEM0_KZM_MARK";

        /// <summary>A circle and a short reason next to a problem object; the size follows the given text height.</summary>
        internal static void Add(Transaction transaction, BlockTableRecord space, XY at, string label, string targetHandle, double height)
        {
            if (!(height > 0)) height = 1;
            double radius = height * 1.5;
            var xdata = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, AppName),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, label ?? string.Empty),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, targetHandle ?? string.Empty));
            var circle = new Circle(new Point3d(at.X, at.Y, 0), Vector3d.ZAxis, radius) { Layer = Layers.Check, XData = xdata };
            space.AppendEntity(circle);
            transaction.AddNewlyCreatedDBObject(circle, true);
            if (string.IsNullOrEmpty(label)) return;
            var text = new DBText
            {
                Layer = Layers.Check,
                TextStyleId = space.Database.Textstyle,
                Position = new Point3d(at.X + radius * 0.75, at.Y + radius * 0.75, 0),
                Height = height,
                TextString = label,
                XData = new ResultBuffer(xdata.AsArray()),
            };
            space.AppendEntity(text);
            transaction.AddNewlyCreatedDBObject(text, true);
        }

        /// <summary>Erases marks in model and paper space; with a target set, only marks of those objects.</summary>
        internal static int Clear(Database database, Transaction transaction, ISet<string> targets = null)
        {
            int count = 0;
            var blocks = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId blockId in blocks)
            {
                var block = (BlockTableRecord)transaction.GetObject(blockId, OpenMode.ForRead);
                if (!block.IsLayout) continue;
                foreach (ObjectId id in block)
                {
                    var entity = (Entity)transaction.GetObject(id, OpenMode.ForRead);
                    using (var xdata = entity.GetXDataForApplication(AppName))
                    {
                        if (xdata == null) continue;
                        if (targets != null)
                        {
                            var values = xdata.AsArray();
                            string target = values.Length > 2 ? values[2].Value as string : null;
                            if (target == null || !targets.Contains(target)) continue;
                        }
                    }
                    if (Layers.IsUsable(database, transaction, entity.Layer, out _))
                    {
                        entity.UpgradeOpen();
                        entity.Erase();
                        count++;
                    }
                }
            }
            return count;
        }
    }

    internal static class Views
    {
        /// <summary>Zooms the current view to the extents, keeping the window proportions.</summary>
        internal static void Zoom(Editor editor, Extents3d extents, double minimumSize)
        {
            using (ViewTableRecord view = editor.GetCurrentView())
            {
                Matrix3d worldToEye = (Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target) *
                                       Matrix3d.Displacement(view.Target - Point3d.Origin) *
                                       Matrix3d.PlaneToWorld(view.ViewDirection)).Inverse();
                extents.TransformBy(worldToEye);
                double width = Math.Max(extents.MaxPoint.X - extents.MinPoint.X, minimumSize);
                double height = Math.Max(extents.MaxPoint.Y - extents.MinPoint.Y, minimumSize);
                double ratio = view.Width / view.Height;
                if (width / height > ratio) height = width / ratio;
                else width = height * ratio;
                view.CenterPoint = new Point2d((extents.MinPoint.X + extents.MaxPoint.X) / 2, (extents.MinPoint.Y + extents.MaxPoint.Y) / 2);
                view.Width = width * 1.6;
                view.Height = height * 1.6;
                editor.SetCurrentView(view);
            }
        }

        internal static void ZoomToObjects(Editor editor, Transaction transaction, IEnumerable<ObjectId> ids, double minimumSize)
        {
            Extents3d? total = null;
            foreach (var id in ids.Where(id => id.IsValid && !id.IsErased))
            {
                if (!(transaction.GetObject(id, OpenMode.ForRead) is Entity entity)) continue;
                Extents3d extents;
                try { extents = entity.GeometricExtents; }
                catch (Autodesk.AutoCAD.Runtime.Exception) { continue; }
                if (total == null) total = extents;
                else { var value = total.Value; value.AddExtents(extents); total = value; }
            }
            if (total.HasValue) Zoom(editor, total.Value, minimumSize);
        }

        internal static void ZoomToPoint(Editor editor, XY point, double size) =>
            Zoom(editor, new Extents3d(new Point3d(point.X - size / 2, point.Y - size / 2, 0),
                new Point3d(point.X + size / 2, point.Y + size / 2, 0)), size);
    }
}
