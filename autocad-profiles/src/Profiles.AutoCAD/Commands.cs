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
            ProfilesRibbon.Start();
        }

        public void Terminate()
        {
            ProfilesRibbon.Stop();
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
