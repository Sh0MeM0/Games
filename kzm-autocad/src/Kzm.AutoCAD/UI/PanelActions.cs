using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Kzm.AutoCAD.Commands;
using Kzm.AutoCAD.Drawing;
using AcadApplication = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Kzm.AutoCAD.UI
{
    /// <summary>
    /// Panel buttons never change the drawing directly. They queue an action and
    /// start KZM_RUN (changes, one UNDO step) or KZM_PICK (pointing in the drawing).
    /// The command dequeues the action and runs it with the document locked by AutoCAD.
    /// </summary>
    internal static class PanelActions
    {
        private sealed class Pending
        {
            internal Document Document;
            internal string Title;
            internal Action<Document> Body;
            internal Action After;
        }

        private static readonly Queue<Pending> RunQueue = new Queue<Pending>();
        private static readonly Queue<Pending> PickQueue = new Queue<Pending>();

        internal static void Run(string title, Action<Document> body, Action after = null) =>
            Send(RunQueue, "KZM_RUN", title, body, after, false);

        internal static void Pick(string title, Action<Document> body, Action after = null) =>
            Send(PickQueue, "KZM_PICK", title, body, after, true);

        private static void Send(Queue<Pending> queue, string command, string title, Action<Document> body, Action after, bool focusDrawing)
        {
            var document = AcadApplication.DocumentManager.MdiActiveDocument;
            if (document == null)
            {
                PanelMessages.Error("нет открытого чертежа.");
                return;
            }
            queue.Enqueue(new Pending { Document = document, Title = title, Body = body, After = after });
            if (focusDrawing) FocusDrawing();
            // Like menu macros (^C^C): cancel a running command first.
            document.SendStringToExecute("\x03\x03" + command + " ", true, false, false);
        }

        internal static void ExecuteRun() => Execute(RunQueue);

        internal static void ExecutePick() => Execute(PickQueue);

        private static void Execute(Queue<Pending> queue)
        {
            var document = AcadApplication.DocumentManager.MdiActiveDocument;
            if (queue.Count == 0) return;
            var pending = queue.Dequeue();
            if (!ReferenceEquals(pending.Document, document))
            {
                document?.Editor.WriteMessage("\nКЗМ: действие панели относится к другому чертежу и не выполнено.");
                return;
            }
            try
            {
                KzmCommands.Run(pending.Title, pending.Body);
            }
            finally
            {
                try { pending.After?.Invoke(); }
                catch (System.Exception exception) { PanelMessages.Error(exception.Message); }
            }
        }

        private static void FocusDrawing()
        {
            try { Autodesk.AutoCAD.Internal.Utils.SetFocusToDwgView(); }
            catch (System.Exception) { /* focus is a convenience only */ }
        }

        /// <summary>Zoom to objects and leave them selected (from the panel, outside commands).</summary>
        internal static void Show(Document document, IEnumerable<ObjectId> ids, double minimumSize)
        {
            if (document == null || !ReferenceEquals(document, AcadApplication.DocumentManager.MdiActiveDocument)) return;
            var alive = ids.Where(id => id.IsValid && !id.IsErased).Distinct().ToArray();
            if (alive.Length == 0)
            {
                PanelMessages.Error("объект удалён из чертежа.");
                return;
            }
            using (document.LockDocument())
            using (var transaction = document.TransactionManager.StartTransaction())
            {
                Views.ZoomToObjects(document.Editor, transaction, alive, minimumSize);
                transaction.Commit();
            }
            document.Editor.SetImpliedSelection(alive);
            FocusDrawing();
        }
    }

    /// <summary>Messages shown in the status line of the panel.</summary>
    internal static class PanelMessages
    {
        internal static event Action<string, bool> Posted;

        internal static void Info(string text) => Posted?.Invoke(text, false);

        internal static void Error(string text) => Posted?.Invoke(text, true);
    }
}
