using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Kzm.AutoCAD.Drawing;
using Kzm.Core;

namespace Kzm.AutoCAD.Services
{
    /// <summary>Triangulated surface built from the point blocks of one surface.</summary>
    internal sealed class SurfaceModel
    {
        internal SurfaceInfo Surface { get; set; }
        internal List<SurfacePoint> Points { get; set; }
        internal List<SurfacePoint> Used { get; set; }
        internal Tin Tin { get; set; }
        internal string Fingerprint { get; set; }
        internal List<string> Problems { get; } = new List<string>();
    }

    /// <summary>
    /// Триангуляция поверхности: точки с читаемой отметкой (кроме исключённых) → TIN Делоне;
    /// внешняя граница (если задана) отсекает треугольники снаружи. Треугольники выводятся
    /// отрезками на слой КЗМ_ТРЕУГ и помечаются XData «SH0MEM0_KZM_TIN» с id поверхности.
    /// </summary>
    internal static class SurfaceModels
    {
        internal const string TinApp = "SH0MEM0_KZM_TIN";

        internal static ObjectId FromHandle(Database database, string handle)
        {
            if (string.IsNullOrEmpty(handle)) return ObjectId.Null;
            if (!long.TryParse(handle, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long value)) return ObjectId.Null;
            try
            {
                var id = database.GetObjectId(false, new Handle(value), 0);
                return id.IsValid && !id.IsErased ? id : ObjectId.Null;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                return ObjectId.Null;
            }
        }

        internal static List<XY> ReadOutline(Database database, Transaction transaction, string handle, string what)
        {
            var id = FromHandle(database, handle);
            if (id.IsNull) throw new KzmException(what + ": полилиния не найдена в чертеже (удалена?). Укажите её заново.");
            return Sources.Outline((Entity)transaction.GetObject(id, OpenMode.ForRead), transaction);
        }

        internal static string OutlineFingerprint(Database database, Transaction transaction, string handle)
        {
            var id = FromHandle(database, handle);
            if (id.IsNull) return "none";
            var fingerprint = new Fingerprint();
            try
            {
                foreach (var p in Sources.Outline((Entity)transaction.GetObject(id, OpenMode.ForRead), transaction)) fingerprint.Add(p.X).Add(p.Y);
            }
            catch (KzmException exception)
            {
                fingerprint.Add(exception.Message);
            }
            return fingerprint.ToString();
        }

        internal static SurfaceModel Build(Database database, Transaction transaction, SurfaceInfo surface, KzmSettings settings)
        {
            var model = new SurfaceModel { Surface = surface, Points = PointBlocks.Read(database, transaction, settings, surface.Id) };
            int broken = model.Points.Count(p => !p.Elevation.IsValid);
            if (broken > 0) model.Problems.Add("точек с нечитаемой отметкой: " + broken + " — в триангуляцию не вошли");
            model.Used = model.Points.Where(p => p.Elevation.IsValid && !p.Data.Excluded).ToList();
            List<XY> boundary = null;
            if (!string.IsNullOrEmpty(surface.BoundaryHandle))
                boundary = ReadOutline(database, transaction, surface.BoundaryHandle, "Граница поверхности «" + surface.Name + "»");
            model.Tin = Tin.Build(model.Used.Select(p => p.Position).ToList(), model.Used.Select(p => p.Elevation.Value.Value).ToList(),
                model.Used.Select(p => p.Handle).ToList(), boundary);
            if (model.Tin.Duplicates.Count > 0)
                model.Problems.Add("точек в одном месте с другой (пропущены): " + model.Tin.Duplicates.Count);
            if (model.Tin.TriangleCount == 0)
                model.Problems.Add("треугольников нет: нужно не меньше 3 точек не на одной прямой");
            model.Fingerprint = PointBlocks.FingerprintOf(model.Points) + "|" + OutlineFingerprint(database, transaction, surface.BoundaryHandle);
            return model;
        }

        internal static int EraseTriangles(Database database, Transaction transaction, string surfaceId)
        {
            int count = 0;
            var blocks = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId blockId in blocks)
            {
                var block = (BlockTableRecord)transaction.GetObject(blockId, OpenMode.ForRead);
                if (!block.IsLayout) continue;
                foreach (ObjectId id in block)
                {
                    if (id.ObjectClass.DxfName != "LINE") continue;
                    var entity = (Entity)transaction.GetObject(id, OpenMode.ForRead);
                    using (var xdata = entity.GetXDataForApplication(TinApp))
                    {
                        if (xdata == null) continue;
                        var values = xdata.AsArray();
                        if (values.Length < 2 || !Equals(values[1].Value, surfaceId)) continue;
                    }
                    if (!Layers.IsUsable(database, transaction, entity.Layer, out string problem))
                        throw new KzmException("треугольники поверхности: " + problem + ".");
                    entity.UpgradeOpen();
                    entity.Erase();
                    count++;
                }
            }
            return count;
        }

        /// <summary>Draws the edges of the TIN in model space (Z = 0).</summary>
        internal static int DrawTriangles(Database database, Transaction transaction, SurfaceModel model)
        {
            EraseTriangles(database, transaction, model.Surface.Id);
            Layers.Ensure(database, transaction, Layers.Triangles, 8);
            PointBlocks.EnsureRegApp(database, transaction, TinApp);
            var space = (BlockTableRecord)transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForWrite);
            int count = 0;
            foreach (var (a, b) in model.Tin.Edges())
            {
                var pa = model.Tin.Point(a);
                var pb = model.Tin.Point(b);
                var line = new Line(new Point3d(pa.X, pa.Y, 0), new Point3d(pb.X, pb.Y, 0))
                {
                    Layer = Layers.Triangles,
                    XData = new ResultBuffer(new TypedValue((int)DxfCode.ExtendedDataRegAppName, TinApp),
                        new TypedValue((int)DxfCode.ExtendedDataAsciiString, model.Surface.Id)),
                };
                space.AppendEntity(line);
                transaction.AddNewlyCreatedDBObject(line, true);
                count++;
            }
            return count;
        }
    }
}
