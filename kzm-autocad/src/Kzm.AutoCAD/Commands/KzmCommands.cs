using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Kzm.AutoCAD.Drawing;
using Kzm.AutoCAD.UI;
using Kzm.Core;
using AcadApplication = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Kzm.AutoCAD.Commands
{
    /// <summary>
    /// Every command is a normal modal AutoCAD command, so all of its changes form
    /// one UNDO group. Data are written in a single transaction at the end; prompts
    /// never run while a write transaction is open.
    /// </summary>
    public sealed partial class KzmCommands
    {
        internal static readonly (string Name, string Description)[] CommandList =
        {
            ("KZM_IMPORT", "импорт отметок из TEXT/MTEXT в поверхность (с отчётом)"),
            ("KZM_SURFACE", "поверхности чертежа: список, новая, текущая, переименовать"),
            ("KZM_DUPLICATES", "решение по точкам ближе допуска совпадения"),
            ("KZM_REPORT", "открыть отчёт последнего импорта"),
            ("KZM_CLEARMARKS", "удалить пометки проверки (слой КЗМ_ПРОВЕРКА)"),
            ("KZM_SETTINGS", "настройки чертежа (шаблон числа, диапазон, R, масштаб, размеры)"),
            ("KZM_MENU", "восстановить верхнее меню «Картограмма» и вкладку ленты"),
            ("KZM_HELP", "этот список"),
        };

        [CommandMethod("KZM_HELP", CommandFlags.Modal)]
        public void Help()
        {
            var editor = AcadApplication.DocumentManager.MdiActiveDocument?.Editor;
            if (editor == null) return;
            editor.WriteMessage("\nКартограмма земляных масс — команды этапа 1:");
            foreach (var (name, description) in CommandList)
                editor.WriteMessage("\n  " + name.PadRight(16) + " — " + description);
            editor.WriteMessage("\nОтметку точки меняют в палитре «Свойства» (Ctrl+1) → Атрибуты → " + PointBlocks.Tag +
                " или двойным щелчком по точке. Точки можно перемещать, копировать и удалять обычными командами.\n");
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

        [CommandMethod("KZM_SETTINGS", CommandFlags.Modal)]
        public void Settings() => Run("настройки", document =>
        {
            var database = document.Database;
            KzmSettings current;
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                current = KzmStore.ReadSettings(database, transaction);
                transaction.Commit();
            }
            KzmSettings updated;
            using (var form = new SettingsForm(current, false, null))
            {
                if (AcadApplication.ShowModalDialog(form) != DialogResult.OK) return;
                updated = form.Result;
            }
            bool sizesChanged = updated.ScaleDenominator != current.ScaleDenominator ||
                                updated.LabelHeightMm != current.LabelHeightMm || updated.PointSizeMm != current.PointSizeMm;
            int resized = 0;
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                KzmStore.WriteSettings(database, transaction, updated);
                if (sizesChanged && updated.ValidateDrawing().Count == 0 && PointBlocks.Read(database, transaction, updated).Count > 0)
                    resized = PointBlocks.ApplySizes(database, transaction, updated);
                transaction.Commit();
            }
            document.Editor.WriteMessage("\nКЗМ: настройки сохранены в чертеже." +
                (resized > 0 ? " Знаки и подписи точек приведены к новым размерам: " + resized + " шт. (ручные смещения подписей сброшены)." : string.Empty));
        });

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

        [CommandMethod("KZM_REPORT", CommandFlags.Modal)]
        public void ShowReport() => Run("отчёт", document =>
        {
            if (!Reports.ShowLast(document))
                document.Editor.WriteMessage("\nКЗМ: в этом сеансе для чертежа ещё нет отчёта. " +
                    "Пометки прошлых проверок остаются на слое " + Layers.Check + ".");
        });

        // ---------- shared helpers ----------

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
            }
            catch (System.Exception exception)
            {
                document.Editor.WriteMessage("\nКЗМ (" + title + "): ошибка: " + exception.Message +
                    " Команда прервана; незавершённая запись в чертёж не выполнена.");
                AcadApplication.ShowAlertDialog("КЗМ — " + title + "\n\n" + exception.Message +
                    "\n\nПодробности — в командной строке AutoCAD.");
            }
        }

        internal static KzmSettings ReadSettings(Database database)
        {
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var settings = KzmStore.ReadSettings(database, transaction);
                transaction.Commit();
                return settings;
            }
        }

        /// <summary>Reads the settings; if values are missing, asks for them in the settings window.</summary>
        internal static KzmSettings EnsureSettings(Document document, bool forImport)
        {
            var database = document.Database;
            KzmSettings settings;
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                settings = KzmStore.ReadSettings(database, transaction);
                transaction.Commit();
            }
            var problems = forImport ? settings.ValidateForImport() : settings.ValidateDrawing();
            if (problems.Count == 0) return settings;
            string reason = "Не хватает настроек чертежа: " + string.Join("; ", problems) + ".\n" +
                            "Модуль не подставляет значения сам — заполните поля. Они сохранятся в этом DWG и будут видны в KZM_SETTINGS.";
            using (var form = new SettingsForm(settings, true, reason))
            {
                if (AcadApplication.ShowModalDialog(form) != DialogResult.OK)
                {
                    document.Editor.WriteMessage("\nКЗМ: настройки не заполнены, команда отменена.");
                    return null;
                }
                settings = form.Result;
            }
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                KzmStore.WriteSettings(database, transaction, settings);
                transaction.Commit();
            }
            return settings;
        }

        /// <summary>Keyword prompt; returns the global keyword, the default on Enter, or null on Esc.</summary>
        internal static string AskKeyword(Editor editor, string message, string defaultGlobal, params (string Global, string Local)[] keywords)
        {
            var options = new PromptKeywordOptions(message) { AllowNone = defaultGlobal != null, AllowArbitraryInput = false };
            foreach (var (global, local) in keywords) options.Keywords.Add(global, local, local);
            if (defaultGlobal != null) options.Keywords.Default = defaultGlobal;
            var result = editor.GetKeywords(options);
            if (result.Status == PromptStatus.None) return defaultGlobal;
            if (result.Status != PromptStatus.OK) return null;
            return Normalize(result.StringResult, keywords) ?? defaultGlobal;
        }

        internal static string Normalize(string value, IEnumerable<(string Global, string Local)> keywords)
        {
            foreach (var (global, local) in keywords)
                if (string.Equals(value, global, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(value, local, StringComparison.OrdinalIgnoreCase))
                    return global;
            return null;
        }

        internal static double ZoomSize(KzmSettings settings) =>
            settings.LabelHeightMm.HasValue && settings.ScaleDenominator.HasValue
                ? settings.LabelHeightMm.Value * settings.UnitsPerPaperMm * 30
                : 20;

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

        internal static string OneLine(string text)
        {
            string value = (text ?? string.Empty).Replace("\r", string.Empty).Replace("\n", " / ").Trim();
            return value.Length > 80 ? value.Substring(0, 77) + "…" : value;
        }
    }
}
