using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Kzm.AutoCAD.Drawing;
using Kzm.Core;
using AcadApplication = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Kzm.AutoCAD.Services
{
    /// <summary>
    /// Отметки узла картограммы связаны (замечание 18): исправили в «Свойствах» проектную или
    /// фактическую — рабочая сразу пересчитывается (Hпр − Hф); исправили рабочую — сразу меняется
    /// проектная (Hф + h). Цвет рабочей — по знаку. Следит постоянно, а не только при открытой панели.
    /// Изменения от UNDO/REDO не трогает, чтобы не мешать отмене.
    /// </summary>
    internal static class NodeSync
    {
        private static readonly HashSet<ObjectId> Pending = new HashSet<ObjectId>();
        private static readonly HashSet<Document> Watched = new HashSet<Document>();
        private static bool busy;
        private static bool undoing;
        private static bool started;

        internal static void Start()
        {
            if (started) return;
            started = true;
            var documents = AcadApplication.DocumentManager;
            foreach (Document document in documents) Watch(document);
            documents.DocumentCreated += (s, e) => Watch(e.Document);
            documents.DocumentToBeDestroyed += (s, e) => Unwatch(e.Document);
            AcadApplication.Idle += OnIdle;
        }

        internal static void Stop()
        {
            if (!started) return;
            started = false;
            AcadApplication.Idle -= OnIdle;
            foreach (var document in Watched.ToList()) Unwatch(document);
        }

        private static void Watch(Document document)
        {
            if (document == null || !Watched.Add(document)) return;
            document.Database.ObjectModified += OnModified;
            document.CommandWillStart += OnCommandWillStart;
            document.CommandEnded += OnCommandEnded;
            document.CommandCancelled += OnCommandEnded;
            document.CommandFailed += OnCommandEnded;
        }

        private static void Unwatch(Document document)
        {
            if (document == null || !Watched.Remove(document)) return;
            document.Database.ObjectModified -= OnModified;
            document.CommandWillStart -= OnCommandWillStart;
            document.CommandEnded -= OnCommandEnded;
            document.CommandCancelled -= OnCommandEnded;
            document.CommandFailed -= OnCommandEnded;
            Pending.RemoveWhere(id => id.Database == document.Database);
        }

        private static bool IsUndo(string name) => name == "U" || name == "UNDO" || name == "REDO" || name == "MREDO" || name == "OOPS";

        private static void OnCommandWillStart(object sender, CommandEventArgs e)
        {
            if (IsUndo(e.GlobalCommandName ?? string.Empty)) undoing = true;
        }

        private static void OnCommandEnded(object sender, CommandEventArgs e)
        {
            if (IsUndo(e.GlobalCommandName ?? string.Empty)) undoing = false;
        }

        private static bool IsNodeTag(string tag) =>
            string.Equals(tag, CartogramService.WorkingTag, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(tag, CartogramService.ProjectTag, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(tag, CartogramService.FactTag, StringComparison.OrdinalIgnoreCase);

        private static void OnModified(object sender, ObjectEventArgs e)
        {
            if (busy || undoing) return;
            try
            {
                if (e.DBObject is AttributeReference attribute && IsNodeTag(attribute.Tag)) Pending.Add(attribute.OwnerId);
            }
            catch (System.Exception) { /* an object being undone or erased */ }
        }

        private static void OnIdle(object sender, EventArgs e)
        {
            if (Pending.Count == 0 || busy) return;
            var document = AcadApplication.DocumentManager.MdiActiveDocument;
            if (document == null || !document.Editor.IsQuiescent) return;
            var ids = Pending.Where(id => id.Database == document.Database && id.IsValid && !id.IsErased).ToList();
            Pending.RemoveWhere(id => id.Database == document.Database || !id.IsValid || id.IsErased);
            if (ids.Count == 0) return;
            busy = true;
            try
            {
                using (document.LockDocument())
                using (var transaction = document.TransactionManager.StartTransaction())
                {
                    foreach (var id in ids)
                        if (transaction.GetObject(id, OpenMode.ForRead) is BlockReference block) Normalize(transaction, block);
                    transaction.Commit();
                }
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nКЗМ: отметки узла не пересчитаны: " + exception.Message);
            }
            finally
            {
                busy = false;
            }
        }

        /// <summary>All node blocks of a cartogram (before a rebuild: the watcher may not have run).</summary>
        internal static int NormalizeCartogram(Database database, Transaction transaction, string cartogramId)
        {
            int count = 0;
            var tracked = KzmStore.ReadCartogramObjects(database, transaction, cartogramId);
            if (tracked == null) return 0;
            busy = true;
            try
            {
                foreach (var id in tracked)
                {
                    if (!id.IsValid || id.IsErased || id.ObjectClass.DxfName != "INSERT") continue;
                    if (transaction.GetObject(id, OpenMode.ForRead) is BlockReference block && Normalize(transaction, block)) count++;
                }
            }
            finally
            {
                busy = false;
            }
            return count;
        }

        /// <summary>
        /// Brings the three attributes of a node block in agreement: the value the user changed (compared with the
        /// texts the module last wrote) is the source. Returns true when something was rewritten.
        /// </summary>
        internal static bool Normalize(Transaction transaction, BlockReference block)
        {
            TypedValue[] values;
            using (var xdata = block.GetXDataForApplication(CartogramService.App))
            {
                if (xdata == null) return false;
                values = xdata.AsArray();
            }
            if (values.Length < 8 || !Equals(values[2].Value, CartogramService.NodeMark)) return false;
            var attributes = Sources.Attributes(block, transaction);
            if (!attributes.TryGetValue(CartogramService.WorkingTag, out var working) || !attributes.TryGetValue(CartogramService.ProjectTag, out var project) ||
                !attributes.TryGetValue(CartogramService.FactTag, out var fact)) return false;
            string w = (working.TextString ?? string.Empty).Trim(), p = (project.TextString ?? string.Empty).Trim(), f = (fact.TextString ?? string.Empty).Trim();
            // Texts written last (0.6 blocks had none: the values of the build).
            string sw, sp, sf;
            if (values.Length >= 14 && values[11].Value is string a && values[12].Value is string b && values[13].Value is string c)
            {
                sw = a.Trim();
                sp = b.Trim();
                sf = c.Trim();
            }
            else
            {
                sf = values[4].Value is double fv ? ElevationFormat.Format(fv, 2) : f;
                sp = values[5].Value is double pv ? ElevationFormat.Format(pv, 2) : p;
                sw = values[6].Value is double wv ? ElevationFormat.FormatSigned(wv, 2) : w;
            }
            if (p == sp && f == sf && w == sw) return false;
            var (newW, newP, newF) = NodeLabels.Reconcile(w, p, f, sw, sp, sf);
            var database = block.Database;
            void Write(AttributeReference attribute, string text)
            {
                if ((attribute.TextString ?? string.Empty).Trim() == text) return;
                if (!attribute.IsWriteEnabled) attribute.UpgradeOpen();
                attribute.TextString = text;
                attribute.AdjustAlignment(database);
            }
            Write(working, newW);
            Write(project, newP);
            Write(fact, newF);
            var color = newW.StartsWith("+", StringComparison.Ordinal) ? Color.FromColorIndex(ColorMethod.ByAci, 1)
                : newW.StartsWith("-", StringComparison.Ordinal) ? Color.FromColorIndex(ColorMethod.ByAci, 5)
                : Color.FromColorIndex(ColorMethod.ByLayer, 256);
            if (!working.Color.Equals(color))
            {
                if (!working.IsWriteEnabled) working.UpgradeOpen();
                working.Color = color;
            }
            // Remember what is shown now (positions of version 0.6 blocks are kept, missing ones filled).
            var list = values.Take(11).ToList();
            while (list.Count < 11) list.Add(new TypedValue((int)DxfCode.ExtendedDataXCoordinate, block.Position));
            list.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, newW));
            list.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, newP));
            list.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, newF));
            if (!block.IsWriteEnabled) block.UpgradeOpen();
            block.XData = new ResultBuffer(list.ToArray());
            return true;
        }
    }
}
