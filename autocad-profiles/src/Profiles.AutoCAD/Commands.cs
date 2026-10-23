using System;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Profiles.Core;
using AcadApplication = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: ExtensionApplication(typeof(Profiles.AutoCAD.ProfilesExtension))]
[assembly: CommandClass(typeof(Profiles.AutoCAD.ProfileCommands))]

namespace Profiles.AutoCAD
{
    public sealed class ProfilesExtension : IExtensionApplication
    {
        public void Initialize()
        {
            try { ProfilesClassicMenu.Start(); }
            catch (System.Exception exception) { StartupError("верхнее меню", exception); }
            try { StartRibbon(); }
            catch (System.Exception exception) { StartupError("ленту", exception); }
        }

        public void Terminate()
        {
            try { ProfilesClassicMenu.Stop(); } catch (System.Exception) { }
            try { ProfilesRibbon.Stop(); } catch (System.Exception) { }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void StartRibbon() => ProfilesRibbon.Start();

        private static void StartupError(string component, System.Exception exception)
        {
            var document = AcadApplication.DocumentManager.MdiActiveDocument;
            document?.Editor.WriteMessage("\nПрофиля: не удалось включить " + component + ": " + exception.Message +
                ". Выполните PROFILI_MENU для восстановления меню; PROFILI_LONG и PROFILI_CROSS открывают построение.");
        }
    }

    public sealed class ProfileCommands
    {
        private sealed class LastProject
        {
            internal ProfileProject Project;
        }

        // Session memory follows the document lifetime. Nothing is written into
        // the drawing or to disk, and one drawing never receives another's data.
        private static readonly ConditionalWeakTable<Document, LastProject> Projects =
            new ConditionalWeakTable<Document, LastProject>();

        [CommandMethod("PROFILI", CommandFlags.Modal)]
        public void OpenProfiles() => Open(ProfileKind.Longitudinal);

        [CommandMethod("PROFILI_LONG", CommandFlags.Modal)]
        public void OpenLongitudinal() => Open(ProfileKind.Longitudinal);

        [CommandMethod("PROFILI_CROSS", CommandFlags.Modal)]
        public void OpenCrossSection() => Open(ProfileKind.CrossSection);

        [CommandMethod("PROFILI_MENU", CommandFlags.Session)]
        public void RestoreMenu()
        {
            try { ProfilesClassicMenu.Restore(); }
            catch (System.Exception exception)
            {
                AcadApplication.ShowAlertDialog("Профиля: не удалось восстановить меню.\n\n" + exception.Message);
            }
            // Classic menus remain usable when the ribbon is hidden or unavailable.
            try { RestoreRibbon(); }
            catch (System.Exception exception)
            {
                var document = AcadApplication.DocumentManager.MdiActiveDocument;
                document?.Editor.WriteMessage("\nПрофиля: лента недоступна: " + exception.Message + ". Используйте верхнее меню.");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RestoreRibbon() => ProfilesRibbon.Restore();

        private static void Open(ProfileKind kind)
        {
            var document = AcadApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;
            var editor = document.Editor;

            try
            {
                Projects.TryGetValue(document, out var previous);
                using (var form = new ProfilesForm(kind, previous?.Project))
                {
                    if (AcadApplication.ShowModalDialog(form) != DialogResult.OK)
                        return;
                    var plan = form.Plan ?? ProfileEngine.Build(form.Project);
                    if (plan.Entities.Count == 0)
                        throw new InvalidOperationException("Нет элементов для построения профиля.");
                    // Remember accepted input before prompting: cancelling the
                    // insertion point must not discard the user's completed form.
                    Projects.GetValue(document, key => new LastProject()).Project = form.Project;

                    var point = editor.GetPoint(new PromptPointOptions(
                        "\nУкажите точку вставки профиля (Enter — отмена): ")
                    {
                        AllowNone = true
                    });
                    if (point.Status != PromptStatus.OK)
                    {
                        editor.WriteMessage("\nВставка отменена. Данные оставлены в памяти этого чертежа до конца сеанса. " +
                            "Откройте " + (kind == ProfileKind.CrossSection ? "PROFILI_CROSS" : "PROFILI_LONG") + ", чтобы продолжить.");
                        return;
                    }

                    // A normal modal AutoCAD command owns one undo group. All new
                    // drawing objects are committed by a single transaction below.
                    var result = DrawingWriter.Insert(document.Database, editor,
                        point.Value, plan);
                    editor.WriteMessage("\nПрофиль построен: {0} объектов. Отменить — ОТМЕНИТЬ / UNDO.", result.EntityCount);
                    foreach (var warning in plan.Warnings)
                        editor.WriteMessage("\nПредупреждение: " + warning);
                    foreach (var warning in result.Warnings)
                        editor.WriteMessage("\nПредупреждение: " + warning);
                    editor.Regen();
                }
            }
            catch (ProfileValidationException exception)
            {
                editor.WriteMessage("\nПрофиль не построен: " + exception.Message);
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\nНе удалось построить профиль: " + exception.Message);
                AcadApplication.ShowAlertDialog("Профиля\n\n" + exception.Message +
                    "\n\nПодробности — в командной строке AutoCAD.");
            }
        }
    }
}
