using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Kzm.AutoCAD.Drawing;
using AcadApplication = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Kzm.AutoCAD.Services
{
    /// <summary>
    /// Пополняет список проектов всех чертежей (замечание 35): чертёж с проектами попадает в него, когда его открывают
    /// (после первого простоя AutoCAD, чтобы не мешать открытию) и когда его сохраняют — под новым именем тоже.
    /// Работает с момента загрузки модуля, даже если панель «КЗМ» не открывали.
    /// </summary>
    internal static class ProjectRegistry
    {
        private static readonly HashSet<Document> Pending = new HashSet<Document>();
        private static readonly HashSet<Database> Watched = new HashSet<Database>();
        private static bool started;

        internal static void Start()
        {
            if (started) return;
            started = true;
            var documents = AcadApplication.DocumentManager;
            foreach (Document document in documents) Watch(document);
            documents.DocumentCreated += OnCreated;
            documents.DocumentToBeDestroyed += OnDestroyed;
            AcadApplication.Idle += OnIdle;
        }

        internal static void Stop()
        {
            if (!started) return;
            started = false;
            var documents = AcadApplication.DocumentManager;
            documents.DocumentCreated -= OnCreated;
            documents.DocumentToBeDestroyed -= OnDestroyed;
            AcadApplication.Idle -= OnIdle;
            foreach (Document document in documents) Unwatch(document);
        }

        private static void OnCreated(object sender, DocumentCollectionEventArgs e) => Watch(e.Document);

        private static void OnDestroyed(object sender, DocumentCollectionEventArgs e) => Unwatch(e.Document);

        private static void Watch(Document document)
        {
            if (document == null || !Watched.Add(document.Database)) return;
            document.Database.SaveComplete += OnSaved;
            Pending.Add(document);
        }

        private static void Unwatch(Document document)
        {
            if (document == null) return;
            Pending.Remove(document);
            if (Watched.Remove(document.Database)) document.Database.SaveComplete -= OnSaved;
        }

        private static void OnSaved(object sender, DatabaseIOEventArgs e)
        {
            try
            {
                if (sender is Database database) ProjectCatalogFile.Register(database, e.FileName);
            }
            catch (Exception)
            {
                // The list is a convenience: saving must never be disturbed by it.
            }
        }

        private static void OnIdle(object sender, EventArgs e)
        {
            if (Pending.Count == 0) return;
            var document = AcadApplication.DocumentManager.MdiActiveDocument;
            if (document == null || !Pending.Contains(document) || !document.Editor.IsQuiescent) return;
            Pending.Remove(document);
            try
            {
                using (document.LockDocument(DocumentLockMode.Read, null, null, false))
                    ProjectCatalogFile.Register(document.Database, document.Database.Filename);
            }
            catch (Exception)
            {
                // Not listed this time; it will be when the drawing is saved.
            }
        }
    }
}
