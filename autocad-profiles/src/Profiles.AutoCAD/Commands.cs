using System;
using System.Linq;
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
            bool built = false;

            try
            {
                // Always reread DWG state so reopening after UNDO cannot revive a
                // stale session cache or another document's project.
                var catalog = DrawingProjectStore.Read(document.Database);
                var previous = catalog.Projects.FirstOrDefault(project =>
                    string.Equals(project.ProjectId, catalog.LastActiveProjectId, StringComparison.OrdinalIgnoreCase))
                    ?? catalog.Projects.FirstOrDefault();
                Action<ProfileProject> saveDraft = project =>
                {
                    // WinForms callbacks run inside AutoCAD's modal loop. Take an
                    // explicit document lock for each short DB write; never hold
                    // a transaction open while the user edits or picks a point.
                    using (document.LockDocument()) DrawingProjectStore.Save(document.Database, project);
                };
                using (var form = new ProfilesForm(kind, previous == null ? null : ProjectFile.Clone(previous),
                    catalog.Projects, saveDraft))
                {
                    if (AcadApplication.ShowModalDialog(form) != DialogResult.OK)
                        return;
                    var plan = form.Plan ?? ProfileEngine.Build(form.Project);
                    if (plan.Entities.Count == 0)
                        throw new InvalidOperationException("Нет элементов для построения профиля.");
                    // Keep accepted numeric input in the DWG even if insertion is
                    // cancelled. The user still saves the DWG normally for disk.
                    saveDraft(form.Project);

                    Autodesk.AutoCAD.Geometry.Point3d? insertionPoint = null;
                    if (!form.RebuildExisting)
                    {
                        var point = editor.GetPoint(new PromptPointOptions(
                            "\nУкажите новую точку вставки профиля (Enter — отмена): ") { AllowNone = true });
                        if (point.Status != PromptStatus.OK)
                        {
                            editor.WriteMessage("\nВставка отменена. Данные проекта оставлены в чертеже. " +
                                "Сохраните DWG (Ctrl+S), чтобы они сохранились после закрытия AutoCAD.");
                            return;
                        }
                        insertionPoint = point.Value;
                    }

                    // A normal modal AutoCAD command owns one undo group. All new
                    // geometry, prior-output erasure and generation metadata are
                    // committed by the writer's single atomic transaction.
                    InsertResult result;
                    using (document.LockDocument())
                        result = DrawingWriter.Insert(document.Database, editor, insertionPoint, plan,
                            form.Project, form.RebuildExisting);
                    built = true;
                    editor.WriteMessage("\nПрофиль «{0}» построен: {1} объектов; заменено прежних: {2}. " +
                        "Отмена — ОТМЕНИТЬ / UNDO. Сохраните DWG (Ctrl+S), чтобы сохранить проекты и построение.",
                        form.Project.Name, result.EntityCount, result.ReplacedEntityCount);
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
                editor.WriteMessage((built ? "\nПрофиль построен, но последующее обновление не завершено: " : "\nНе удалось построить профиль: ") + exception.Message);
                AcadApplication.ShowAlertDialog("Профиля\n\n" + exception.Message +
                    "\n\nПодробности — в командной строке AutoCAD.");
            }
        }
    }
}
