using System;
using System.IO;
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
                Action<ProfileProject> saveDraft = project =>
                {
                    // WinForms callbacks run inside AutoCAD's modal loop. Take an
                    // explicit document lock for each short DB write; never hold
                    // a transaction open while the user edits or picks a point.
                    using (document.LockDocument()) DrawingProjectStore.Save(document.Database, project);
                };
                Action<ProfileProject, string> saveSerialized = (project, json) =>
                {
                    using (document.LockDocument()) DrawingProjectStore.Save(document.Database, project, json);
                };
                // Every project is also kept in the per-user autosave library, so
                // work survives switching drawings, UNDO and an unsaved DWG.
                var autosave = OpenAutosave(document, catalog, saveSerialized);
                var available = autosave?.Sync.DrawingProjects ?? catalog.Projects;
                var previous = available.FirstOrDefault(project =>
                    string.Equals(project.ProjectId, catalog.LastActiveProjectId, StringComparison.OrdinalIgnoreCase))
                    ?? available.FirstOrDefault();
                if (previous == null && autosave != null && autosave.Sync.LibraryOnly.Count > 0)
                {
                    // A drawing without projects continues the most recent work.
                    try { previous = autosave.Library.Load(autosave.Sync.LibraryOnly[0].ProjectId); }
                    catch (ProfileValidationException exception) { editor.WriteMessage("\nПрофиля: " + exception.Message); }
                }
                Action<ProfileProject> remember = project =>
                {
                    if (autosave == null) return;
                    try
                    {
                        string key = autosave.DrawingKey();
                        autosave.Library.Save(project, null, key, autosave.DrawingPath);
                    }
                    catch (ProfileValidationException exception) { editor.WriteMessage("\nПрофиля: " + exception.Message); }
                };
                Action<string> deleteProject = projectId =>
                {
                    using (document.LockDocument()) DrawingProjectStore.Delete(document.Database, projectId);
                };
                ProfileProject active = previous == null ? null : ProjectFile.Clone(previous);
                string selectedSectionId = null;
                var captureOptions = new DrawingCaptureOptions();
                ProfileEditorSession session = null;
                bool captureAccepted = false, captureSaved = false;
                while (true)
                {
                    DrawingCaptureRequest captureRequest;
                    ProfileProject project;
                    DrawingPlan plan;
                    bool rebuildExisting, allSections, allProfiles;
                    DialogResult dialogResult;
                    using (var form = new ProfilesForm(kind, active, available, saveDraft, selectedSectionId, deleteProject, autosave))
                    {
                        form.CaptureOptions = captureOptions.Clone();
                        if (session != null) form.RestoreEditorSession(session, selectedSectionId);
                        if (captureAccepted) form.AcceptDrawingCapture(captureSaved);
                        captureAccepted = false;
                        dialogResult = AcadApplication.ShowModalDialog(form);
                        if (dialogResult != DialogResult.OK && dialogResult != DialogResult.Retry) return;
                        captureRequest = form.CaptureRequest;
                        project = form.Project;
                        plan = form.Plan;
                        rebuildExisting = form.RebuildExisting;
                        selectedSectionId = form.SelectedSectionId;
                        allSections = form.AllSections;
                        allProfiles = form.AllProfiles;
                        session = form.CaptureEditorSession();
                    }
                    // No WinForms window or database transaction remains open
                    // while AutoCAD asks for geometry, labels or confirmation.
                    if (dialogResult == DialogResult.Retry && captureRequest != null)
                    {
                        active = captureRequest.Draft;
                        selectedSectionId = captureRequest.SelectedSectionId;
                        kind = active.Kind;
                        try
                        {
                            var captured = DrawingCapture.Run(document, captureRequest);
                            if (captured.Accepted)
                            {
                                active = captured.Project;
                                selectedSectionId = captured.SelectedSectionId;
                                captureOptions = captured.Options.Clone();
                                captureAccepted = true;
                                captureSaved = false;
                                try
                                {
                                    saveDraft(active);
                                    captureSaved = true;
                                    remember(active);
                                    editor.WriteMessage("\nНабранные данные сохранены в проекте чертежа. Для записи на диск сохраните DWG (Ctrl+S).");
                                }
                                catch (System.Exception exception)
                                {
                                    // Keep the accepted points in the reopened
                                    // form even if the DWG is temporarily read-only.
                                    remember(active);
                                    editor.WriteMessage("\nДанные перенесены в окно, но пока не сохранены в DWG: " + exception.Message +
                                        (autosave != null ? " Они сохранены в автосохранении." : ""));
                                }
                            }
                        }
                        catch (ProfileValidationException exception)
                        {
                            editor.WriteMessage("\nНабор не перенесён: " + exception.Message + " Исходный проект сохранён.");
                        }
                        catch (System.Exception exception)
                        {
                            editor.WriteMessage("\nНе удалось завершить набор: " + exception.Message + " Исходный проект сохранён.");
                        }
                        continue;
                    }
                    if (dialogResult != DialogResult.OK || project == null) return;
                    plan = plan ?? ProfileBuildScope.Build(project, selectedSectionId, allSections, allProfiles);
                    if (plan.Entities.Count == 0)
                        throw new InvalidOperationException("Нет элементов для построения профиля.");
                    // Keep accepted numeric input in the DWG even if insertion is
                    // cancelled. The user still saves the DWG normally for disk.
                    saveDraft(project);
                    remember(project);

                    Autodesk.AutoCAD.Geometry.Point3d? insertionPoint = null;
                    if (!rebuildExisting)
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
                            project, rebuildExisting, selectedSectionId, allSections, allProfiles);
                    built = true;
                    editor.WriteMessage("\nПрофиль «{0}» построен: {1} объектов; заменено прежних: {2}. " +
                        "Отмена — ОТМЕНИТЬ / UNDO. Сохраните DWG (Ctrl+S), чтобы сохранить проекты и построение.",
                        project.Name, result.EntityCount, result.ReplacedEntityCount);
                    foreach (var warning in plan.Warnings)
                        editor.WriteMessage("\nПредупреждение: " + warning);
                    foreach (var warning in result.Warnings)
                        editor.WriteMessage("\nПредупреждение: " + warning);
                    editor.Regen();
                    return;
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

        private static ProfilesAutosave OpenAutosave(Document document, DrawingProjectCatalog catalog, Action<ProfileProject, string> saveSerialized)
        {
            try
            {
                var library = new ProjectLibrary(ProjectLibrary.DefaultRoot());
                // The document name is the full path of a saved DWG («Drawing1.dwg» for a new one).
                string drawingPath = document.Name;
                string drawingKey = DrawingProjectStore.TryDrawingId(document.Database);
                var sync = library.Synchronize(catalog.Projects, drawingKey, drawingPath);
                foreach (string message in sync.Messages)
                    document.Editor.WriteMessage("\nПрофиля: " + message);
                return new ProfilesAutosave(library, sync, drawingPath, () =>
                {
                    using (document.LockDocument()) return DrawingProjectStore.TryDrawingId(document.Database);
                }, saveSerialized);
            }
            catch (System.Exception exception) when (exception is ProfileValidationException || exception is IOException ||
                                                      exception is UnauthorizedAccessException || exception is ArgumentException)
            {
                // The editor works with the DWG alone; nothing already stored is changed.
                document.Editor.WriteMessage("\nПрофиля: автосохранение недоступно (" + exception.Message + "). Данные сохраняются только в DWG.");
                return null;
            }
        }
    }
}
