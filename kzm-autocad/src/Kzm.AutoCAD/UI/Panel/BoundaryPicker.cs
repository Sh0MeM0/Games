using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Kzm.AutoCAD.Drawing;
using Kzm.Core;

namespace Kzm.AutoCAD.UI.Panel
{
    /// <summary>Selection of one or several closed outlines for a boundary (замечание 31).</summary>
    internal static class BoundaryPicker
    {
        private static readonly SelectionFilter Curves = new SelectionFilter(new[]
        {
            new TypedValue((int)DxfCode.Start, "LWPOLYLINE,POLYLINE,CIRCLE,SPLINE,ELLIPSE"),
        });

        /// <summary>Handles of the selected closed outlines through «;», or null when nothing usable was selected.</summary>
        internal static string Pick(Document document, string prompt)
        {
            var options = new PromptSelectionOptions { MessageForAdding = prompt, AllowDuplicates = false };
            var selection = document.Editor.GetSelection(options, Curves);
            if (selection.Status != PromptStatus.OK) return null;
            var handles = new List<string>();
            var skipped = new List<string>();
            using (var transaction = document.Database.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (var id in selection.Value.GetObjectIds())
                {
                    try
                    {
                        Sources.Outline((Entity)transaction.GetObject(id, OpenMode.ForRead), transaction);
                        handles.Add(id.Handle.ToString());
                    }
                    catch (KzmException exception)
                    {
                        skipped.Add(id.Handle + ": " + exception.Message);
                    }
                }
                transaction.Commit();
            }
            foreach (var reason in skipped) document.Editor.WriteMessage("\nКЗМ: пропущена " + reason);
            return BoundaryList.Join(handles);
        }
    }
}
