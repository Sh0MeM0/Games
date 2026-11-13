using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Windows;
using Kzm.AutoCAD.Drawing;
using AcadApplication = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Kzm.AutoCAD.UI.Panel
{
    /// <summary>
    /// The dockable side panel «КЗМ». It follows the active drawing and refreshes
    /// itself after changes of the module's points (also after MOVE/ERASE/UNDO).
    /// </summary>
    internal static class KzmPalette
    {
        private static readonly Guid PaletteId = new Guid("6C0F2E8A-5B1D-4C7E-9A3F-2D84B1E7C590");
        private static PaletteSet palette;
        private static KzmPanel panel;
        private static bool dirty;
        /// <summary>Drawings whose module data changed since the panel read them.</summary>
        private static readonly HashSet<Database> Changed = new HashSet<Database>();
        private static readonly HashSet<Document> Watched = new HashSet<Document>();

        internal static KzmPanel Open()
        {
            if (palette == null)
            {
                panel = new KzmPanel();
                palette = new PaletteSet("КЗМ — картограмма земляных масс", "KZM", PaletteId)
                {
                    Style = PaletteSetStyles.ShowCloseButton | PaletteSetStyles.ShowAutoHideButton | PaletteSetStyles.ShowPropertiesMenu,
                    MinimumSize = new Size(360, 520),
                    DockEnabled = DockSides.Left | DockSides.Right | DockSides.None,
                };
                palette.Add("Картограмма", panel);
                palette.StateChanged += (s, e) => { if (palette.Visible) MarkDirty(); };
                StartWatching();
                palette.Visible = true;
                // Dock only after the palette is visible; later AutoCAD keeps the user's layout.
                if (FirstShow()) palette.Dock = DockSides.Right;
            }
            palette.Visible = true;
            panel.RefreshAll();
            return panel;
        }

        internal static bool IsVisible => palette != null && palette.Visible;

        /// <summary>The panel should be redrawn (e.g. another drawing became active); the data is re-read only if it changed.</summary>
        internal static void MarkDirty() => dirty = true;

        /// <summary>The module data of this drawing changed: re-read it at the next refresh.</summary>
        internal static void MarkChanged(Database database)
        {
            if (database != null) Changed.Add(database);
            dirty = true;
        }

        internal static void MarkChanged() => MarkChanged(AcadApplication.DocumentManager.MdiActiveDocument?.Database);

        /// <summary>Docks to the right on the very first use only; later the user's layout is kept by AutoCAD.</summary>
        private static bool FirstShow()
        {
            try
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sh0MeM0", "Kzm");
                string flag = Path.Combine(folder, "panel-docked.flag");
                if (File.Exists(flag)) return false;
                Directory.CreateDirectory(folder);
                File.WriteAllText(flag, "1");
                return true;
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        private static void StartWatching()
        {
            var documents = AcadApplication.DocumentManager;
            documents.DocumentActivated += (s, e) => { Watch(e.Document); MarkDirty(); };
            documents.DocumentCreated += (s, e) => Watch(e.Document);
            documents.DocumentToBeDestroyed += (s, e) => Unwatch(e.Document);
            foreach (Document document in documents) Watch(document);
            AcadApplication.Idle += OnIdle;
        }

        private static void Watch(Document document)
        {
            if (document == null || !Watched.Add(document)) return;
            document.CommandEnded += OnCommandEnded;
            document.CommandCancelled += OnCommandEnded;
            var database = document.Database;
            database.ObjectAppended += OnObjectChanged;
            database.ObjectErased += OnObjectErased;
            database.ObjectModified += OnObjectChanged;
        }

        private static void Unwatch(Document document)
        {
            if (document == null || !Watched.Remove(document)) return;
            document.CommandEnded -= OnCommandEnded;
            document.CommandCancelled -= OnCommandEnded;
            var database = document.Database;
            database.ObjectAppended -= OnObjectChanged;
            database.ObjectErased -= OnObjectErased;
            database.ObjectModified -= OnObjectChanged;
            panel?.Forget(document);
            Changed.Remove(database);
            MarkDirty();
        }

        private static void OnCommandEnded(object sender, CommandEventArgs e)
        {
            string name = e.GlobalCommandName ?? string.Empty;
            // UNDO/REDO restore the module's records without touching our events reliably.
            if (name == "U" || name == "UNDO" || name == "REDO" || name == "MREDO" || name == "OOPS" || name == "KZM_RUN" || name == "KZM_UPDATE" ||
                name == "KZM_POINT")
                MarkChanged((sender as Document)?.Database);
        }

        private static void OnObjectErased(object sender, ObjectErasedEventArgs e)
        {
            if (e.DBObject is BlockReference) MarkChanged(sender as Database);
        }

        private static void OnObjectChanged(object sender, ObjectEventArgs e)
        {
            var database = sender as Database;
            if (database != null && Changed.Contains(database)) return;
            try
            {
                var obj = e.DBObject;
                if (obj is AttributeReference attribute)
                {
                    // Elevation of a point, or an elevation typed on a cartogram node (the cartogram becomes outdated).
                    string tag = attribute.Tag;
                    if (string.Equals(tag, PointBlocks.Tag, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(tag, Services.CartogramService.ProjectTag, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(tag, Services.CartogramService.FactTag, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(tag, Services.CartogramService.WorkingTag, StringComparison.OrdinalIgnoreCase)) MarkChanged(database);
                }
                else if (obj is BlockReference reference)
                {
                    using (var xdata = reference.GetXDataForApplication(PointBlocks.AppName))
                        if (xdata != null) MarkChanged(database);
                }
            }
            catch (System.Exception)
            {
                MarkChanged(database);
            }
        }

        private static void OnIdle(object sender, EventArgs e)
        {
            if (!dirty || !IsVisible || panel == null) return;
            var document = AcadApplication.DocumentManager.MdiActiveDocument;
            if (document != null && !document.Editor.IsQuiescent) return;
            dirty = false;
            bool reread = document == null || Changed.Remove(document.Database);
            try { panel.RefreshAll(reread); }
            catch (System.Exception exception) { PanelMessages.Error(exception.Message); }
        }
    }
}
