using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Kzm.AutoCAD.Drawing;
using Kzm.AutoCAD.UI;
using Kzm.AutoCAD.UI.Panel;
using Kzm.Core;
using AcadApplication = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Kzm.AutoCAD.Commands
{
    /// <summary>
    /// The work is done in the КЗМ side panel. Its buttons run the internal
    /// commands KZM_RUN (changes: one command = one UNDO step) and KZM_PICK
    /// (pointing at objects in the drawing; no changes, no UNDO mark).
    /// </summary>
    public sealed class KzmCommands
    {
        internal static readonly (string Name, string Description)[] CommandList =
        {
            ("KZM", "открыть панель «КЗМ» (поверхности, точки, импорт, настройки)"),
            ("KZM_IMPORT", "открыть панель на форме импорта отметок"),
            ("KZM_SETTINGS", "открыть панель на настройках чертежа"),
            ("KZM_CLEARMARKS", "удалить пометки проверки (слой КЗМ_ПРОВЕРКА)"),
            ("KZM_MENU", "восстановить верхнее меню «Картограмма» и вкладку ленты"),
            ("KZM_HELP", "этот список"),
        };

        [CommandMethod("KZM", CommandFlags.Modal)]
        public void OpenPanel() => Guarded(() => KzmPalette.Open());

        [CommandMethod("KZM_IMPORT", CommandFlags.Modal)]
        public void OpenImport() => Guarded(() => KzmPalette.Open().ShowImport(null));

        [CommandMethod("KZM_SETTINGS", CommandFlags.Modal)]
        public void OpenSettings() => Guarded(() => KzmPalette.Open().ShowSettings());

        [CommandMethod("KZM_RUN", CommandFlags.Modal | CommandFlags.NoHistory | CommandFlags.Redraw)]
        public void RunPanelAction() => PanelActions.ExecuteRun();

        [CommandMethod("KZM_PICK", CommandFlags.Modal | CommandFlags.NoHistory | CommandFlags.NoUndoMarker)]
        public void PickForPanel() => PanelActions.ExecutePick();

        [CommandMethod("KZM_HELP", CommandFlags.Modal)]
        public void Help()
        {
            var editor = AcadApplication.DocumentManager.MdiActiveDocument?.Editor;
            if (editor == null) return;
            editor.WriteMessage("\nКартограмма земляных масс — команды:");
            foreach (var (name, description) in CommandList)
                editor.WriteMessage("\n  " + name.PadRight(16) + " — " + description);
            editor.WriteMessage("\nОсновная работа — в панели «КЗМ» (команда KZM, меню «Картограмма» или вкладка ленты «КЗМ»).\n");
        }

        [CommandMethod("KZM_MENU", CommandFlags.Session)]
        public void RestoreMenu()
        {
            try { KzmClassicMenu.Restore(); }
            catch (System.Exception exception)
            {
                AcadApplication.ShowAlertDialog("КЗМ: не удалось восстановить меню.\n\n" + exception.Message);
            }
            try { KzmExtension.RestoreRibbon(); }
            catch (System.Exception exception)
            {
                AcadApplication.DocumentManager.MdiActiveDocument?.Editor.WriteMessage(
                    "\nКЗМ: лента недоступна: " + exception.Message + ". Используйте верхнее меню.");
            }
        }

        [CommandMethod("KZM_CLEARMARKS", CommandFlags.Modal)]
        public void ClearMarks() => Run("очистка пометок", document =>
        {
            int count;
            using (var transaction = document.Database.TransactionManager.StartTransaction())
            {
                count = CheckMarks.Clear(document.Database, transaction);
                transaction.Commit();
            }
            document.Editor.WriteMessage("\nКЗМ: удалено пометок проверки: " + count + ".");
        });

        // ---------- shared helpers ----------

        private static void Guarded(Action action)
        {
            try { action(); }
            catch (System.Exception exception)
            {
                AcadApplication.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\nКЗМ: не удалось открыть панель: " + exception.Message);
            }
        }

        internal static void Run(string title, Action<Document> body)
        {
            var document = AcadApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            try
            {
                body(document);
            }
            catch (KzmException exception)
            {
                document.Editor.WriteMessage("\nКЗМ (" + title + "): " + exception.Message + " Чертёж не изменён.");
                PanelMessages.Error(title + ": " + exception.Message);
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nКЗМ (" + title + "): ошибка: " + exception.Message +
                    " Действие прервано; незавершённая запись в чертёж не выполнена.");
                PanelMessages.Error(title + ": " + exception.Message);
            }
        }

        internal static void Highlight(Database database, IEnumerable<ObjectId> ids, bool on)
        {
            using (var transaction = database.TransactionManager.StartOpenCloseTransaction())
            {
                foreach (var id in ids.Where(id => id.IsValid && !id.IsErased))
                {
                    if (!(transaction.GetObject(id, OpenMode.ForRead) is Entity entity)) continue;
                    if (on) entity.Highlight(); else entity.Unhighlight();
                }
                transaction.Commit();
            }
        }

        internal static double ZoomSize(KzmSettings settings) =>
            settings.LabelHeightMm.HasValue && settings.ScaleDenominator.HasValue
                ? settings.LabelHeightMm.Value * settings.UnitsPerPaperMm * 30
                : 20;

        internal static string OneLine(string text)
        {
            string value = (text ?? string.Empty).Replace("\r", string.Empty).Replace("\n", " / ").Trim();
            return value.Length > 80 ? value.Substring(0, 77) + "…" : value;
        }
    }
}
