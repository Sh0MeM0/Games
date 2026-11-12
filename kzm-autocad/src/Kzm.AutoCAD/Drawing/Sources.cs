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
                return new SourceText(handle, SourceKind.MText, contents, TextCleaner.CleanMText(contents), Plan(mtext.Location));
            }
            if (entity is DBText text)
            {
                string value = text.TextString ?? string.Empty;
                return new SourceText(handle, SourceKind.Text, value, TextCleaner.CleanText(value), Plan(TextAnchor(text)));
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

        internal static bool SameHeight(double a, double b) => Math.Abs(a - b) <= 1e-9 * Math.Max(1.0, Math.Max(Math.Abs(a), Math.Abs(b)));
    }
}
