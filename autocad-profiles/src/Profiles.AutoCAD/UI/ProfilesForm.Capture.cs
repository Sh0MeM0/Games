using System;
using System.Windows.Forms;
using Profiles.Core;

namespace Profiles.AutoCAD
{
    public sealed partial class ProfilesForm
    {
        internal DrawingCaptureRequest CaptureRequest { get; private set; }
        internal DrawingCaptureOptions CaptureOptions { get; set; } = new DrawingCaptureOptions();

        internal void AcceptDrawingCapture(bool savedToDrawing)
        {
            SetDirty();
            if (!savedToDrawing)
            {
                SetStatus("Данные получены из чертежа и сохранены в редакторе. Запись в DWG не завершена; повторите сохранение перед закрытием.", true);
                return;
            }
            if (autoSaveJson.Checked && currentFile != null)
            {
                // The restored checkbox is the user's earlier authorization to
                // update this particular JSON file. Capture never enables it.
                if (!TrySaveCurrent(false)) return;
            }
            else
            {
                dirty = false;
                autoSaveTimer.Stop();
                memoryOnlyProjects.Remove(currentProjectId);
                UpdateTitle();
            }
            SetStatus("Данные взяты из чертежа. Проверьте отметки и назначения точек; после работы сохраните DWG.");
        }

        private void RequestDrawingCapture(ProfileKind kind)
        {
            try
            {
                // Numeric drafts need not be geometrically complete, but invalid
                // cells must stay visible instead of disappearing on modal exit.
                var draft = ProjectFile.Clone(ReadProject());
                var options = UI.DrawingCaptureOptionsDialog.ShowOptions(this, kind, CaptureOptions);
                if (options == null) return;
                PersistProject(draft, true);
                Project = draft;
                CaptureRequest = new DrawingCaptureRequest
                {
                    Kind = kind, Draft = ProjectFile.Clone(draft),
                    SelectedSectionId = SelectedSectionId, Options = options
                };
                // The command adapter performs selection only after this modal
                // window has closed, then restores its complete editor session.
                accepted = true;
                DialogResult = DialogResult.Retry;
                Close();
            }
            catch (Exception error) { ShowError(error.Message); }
        }
    }
}
