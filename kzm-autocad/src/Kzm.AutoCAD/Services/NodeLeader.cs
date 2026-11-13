using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Kzm.AutoCAD.Drawing;

namespace Kzm.AutoCAD.Services
{
    /// <summary>
    /// Выноска отметок узла (замечание 25). Блок отметок тянут за ручку вставки — все три значения переезжают вместе,
    /// а модуль рисует выноску, как в образце пользователя: линия от узла до угла подписей и полка между проектной
    /// (над полкой) и фактической (под ней) отметками; рабочая остаётся слева от начала полки. Ручку вернули в узел —
    /// выноска убирается. Выноска — обычная полилиния 0,25 мм на слое КЗМ_УЗЛЫ: видна и без модуля.
    /// </summary>
    internal static class NodeLeader
    {
        internal const string Mark = "LEADER";
        /// <summary>XData of a node block: [14] — where the module put the block, [15] — handle of its leader.</summary>
        internal const int PlacedIndex = 14;
        internal const int LeaderIndex = 15;

        /// <summary>Node → corner of the labels (insertion point of the block) → end of the shelf under the right-hand labels.</summary>
        internal static Point3d[] Vertices(BlockReference block, Point3d node, Transaction transaction)
        {
            var corner = block.Position;
            double right = corner.X, height = 0;
            foreach (var attribute in Sources.Attributes(block, transaction).Values)
            {
                height = Math.Max(height, attribute.Height);
                try
                {
                    var extents = attribute.GeometricExtents;
                    if (extents.MinPoint.X >= corner.X - 1e-9) right = Math.Max(right, extents.MaxPoint.X);
                }
                catch (Autodesk.AutoCAD.Runtime.Exception)
                {
                    // An empty or unreadable text: the shelf keeps its minimal length.
                }
            }
            if (!(height > 0)) height = 1;
            double end = Math.Max(right + 0.35 * height, corner.X + 2 * height);
            return new[] { new Point3d(node.X, node.Y, 0), new Point3d(corner.X, corner.Y, 0), new Point3d(end, corner.Y, 0) };
        }

        internal static Polyline Create(Transaction transaction, BlockReference block, Point3d node, string cartogramId, string key, BlockTableRecord space)
        {
            var vertices = Vertices(block, node, transaction);
            var leader = new Polyline { Layer = Layers.Nodes, LineWeight = CartogramPainter.TextWeight };
            for (int i = 0; i < vertices.Length; i++) leader.AddVertexAt(i, new Point2d(vertices[i].X, vertices[i].Y), 0, 0, 0);
            leader.XData = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, CartogramService.App),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, cartogramId),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, Mark),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, key));
            space.AppendEntity(leader);
            transaction.AddNewlyCreatedDBObject(leader, true);
            return leader;
        }

        /// <summary>Writes the handle of the leader (or an empty text) into the XData of the node block.</summary>
        internal static void Remember(BlockReference block, Entity leader)
        {
            TypedValue[] values;
            using (var xdata = block.GetXDataForApplication(CartogramService.App)) values = xdata?.AsArray();
            if (values == null || values.Length < 14) return;
            var list = values.ToList();
            if (list.Count <= PlacedIndex) list.Add(new TypedValue((int)DxfCode.ExtendedDataXCoordinate, values[7].Value is Point3d anchor ? anchor : block.Position));
            var handle = new TypedValue((int)DxfCode.ExtendedDataAsciiString, leader?.Handle.ToString() ?? string.Empty);
            if (list.Count <= LeaderIndex) list.Add(handle);
            else list[LeaderIndex] = handle;
            if (!block.IsWriteEnabled) block.UpgradeOpen();
            block.XData = new ResultBuffer(list.ToArray());
        }

        /// <summary>
        /// After the block was moved: the leader follows it (created, updated or removed). <paramref name="gridShift"/> — how far
        /// the user moved the whole cartogram with its grid: then the labels did not leave their node. Returns true when the
        /// drawing changed.
        /// </summary>
        internal static bool Follow(Transaction transaction, BlockReference block, Vector3d gridShift)
        {
            TypedValue[] values;
            using (var xdata = block.GetXDataForApplication(CartogramService.App)) values = xdata?.AsArray();
            if (values == null || values.Length < 14 || !(values[1].Value is string cartogramId) || !(values[3].Value is string key) ||
                !(values[7].Value is Point3d anchor)) return false;
            // The user turned the finished drawing: nothing is redrawn.
            if (Math.Abs(Math.IEEERemainder(block.Rotation, 2 * Math.PI)) > 1e-6) return false;
            var database = block.Database;
            var node = anchor + gridShift;
            string handle = values.Length > LeaderIndex ? values[LeaderIndex].Value as string : null;
            var leaderId = SurfaceModels.FromHandle(database, handle);
            var leader = !leaderId.IsNull && !leaderId.IsErased ? transaction.GetObject(leaderId, OpenMode.ForRead) as Polyline : null;
            double height = Sources.Attributes(block, transaction).Values.Select(a => a.Height).DefaultIfEmpty(1).Max();
            var shift = block.Position - node;
            if (shift.Length <= 0.5 * height)
            {
                // Back onto the node: the labels snap onto it exactly, the leader goes.
                bool changed = false;
                if (shift.Length > 1e-9)
                {
                    if (!block.IsWriteEnabled) block.UpgradeOpen();
                    block.TransformBy(Matrix3d.Displacement(node - block.Position));
                    changed = true;
                }
                if (leader != null)
                {
                    leader.UpgradeOpen();
                    leader.Erase();
                    Remember(block, null);
                    changed = true;
                }
                return changed;
            }
            var vertices = Vertices(block, node, transaction);
            if (leader != null)
            {
                if (Same(leader, vertices)) return false;
                leader.UpgradeOpen();
                while (leader.NumberOfVertices > vertices.Length) leader.RemoveVertexAt(leader.NumberOfVertices - 1);
                for (int i = 0; i < vertices.Length; i++)
                {
                    var point = new Point2d(vertices[i].X, vertices[i].Y);
                    if (i < leader.NumberOfVertices) leader.SetPointAt(i, point);
                    else leader.AddVertexAt(i, point, 0, 0, 0);
                }
                return true;
            }
            var space = (BlockTableRecord)transaction.GetObject(block.OwnerId, OpenMode.ForWrite);
            leader = Create(transaction, block, node, cartogramId, key, space);
            Remember(block, leader);
            // The leader belongs to the cartogram: a rebuild or «Удалить картограмму» removes it with the rest.
            var tracked = KzmStore.ReadCartogramObjects(database, transaction, cartogramId) ?? new List<ObjectId>();
            tracked.Add(leader.ObjectId);
            KzmStore.WriteCartogramObjects(database, transaction, cartogramId, tracked);
            return true;
        }

        private static bool Same(Polyline leader, Point3d[] vertices)
        {
            if (leader.NumberOfVertices != vertices.Length) return false;
            for (int i = 0; i < vertices.Length; i++)
                if (leader.GetPoint2dAt(i).GetDistanceTo(new Point2d(vertices[i].X, vertices[i].Y)) > 1e-9) return false;
            return true;
        }
    }
}
