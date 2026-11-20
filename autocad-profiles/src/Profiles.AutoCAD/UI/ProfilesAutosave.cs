using System;
using System.Globalization;
using Profiles.Core;

namespace Profiles.AutoCAD
{
    /// <summary>
    /// Connects the editor to the per-user autosave library and to the drawing
    /// it was opened from. Every accepted change is written to the library as
    /// well as to the DWG, so switching drawings, UNDO or closing a DWG without
    /// saving never loses entered data.
    /// </summary>
    public sealed class ProfilesAutosave
    {
        private readonly Func<string> drawingKey;
        private string cachedKey;

        public ProfilesAutosave(ProjectLibrary library, LibrarySyncResult sync, string drawingName, Func<string> drawingKey,
            Action<ProfileProject, string> saveSerializedToDrawing = null)
        {
            Library = library ?? throw new ArgumentNullException(nameof(library));
            Sync = sync ?? new LibrarySyncResult();
            DrawingName = drawingName;
            this.drawingKey = drawingKey;
            SaveSerializedToDrawing = saveSerializedToDrawing;
        }

        public ProjectLibrary Library { get; }
        public LibrarySyncResult Sync { get; }
        public string DrawingName { get; }

        /// <summary>Optional DWG writer that reuses the JSON already produced by the editor.</summary>
        public Action<ProfileProject, string> SaveSerializedToDrawing { get; }

        /// <summary>Stable identifier of the drawing's project catalog; null until the catalog exists.</summary>
        public string DrawingKey()
        {
            if (cachedKey != null || drawingKey == null) return cachedKey;
            try { cachedKey = drawingKey(); }
            catch (Exception) { cachedKey = null; }
            return cachedKey;
        }

        internal static string LocalTime(DateTime utc, bool seconds = false) =>
            DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString(seconds ? "dd.MM.yyyy HH:mm:ss" : "dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
    }
}
