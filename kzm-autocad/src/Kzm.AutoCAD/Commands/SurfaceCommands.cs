using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Kzm.AutoCAD.Drawing;
using Kzm.Core;

namespace Kzm.AutoCAD.Commands
{
    public sealed partial class KzmCommands
    {
        [CommandMethod("KZM_SURFACE", CommandFlags.Modal)]
        public void Surface() => Run("поверхности", document =>
        {
            var editor = document.Editor;
            var database = document.Database;
            // Listing and naming surfaces needs no sizes: do not force the settings window here.
            var settings = ReadSettings(database);
            var (surfaces, currentId, counts) = ReadSurfaceList(database, settings);
            PrintSurfaces(editor, surfaces, currentId, counts);
            string action = AskKeyword(editor, "\nДействие", "Exit",
                ("New", "Новая"), ("Current", "Текущая"), ("Rename", "Переименовать"), ("Exit", "Выход"));
            if (action == null || action == "Exit") return;

            if (action == "New")
            {
                var created = AskNewSurface(document, surfaces);
                if (created == null) return;
                Save(database, created, true, true);
                editor.WriteMessage("\nКЗМ: создана поверхность " + created.Title + "; она текущая.");
                return;
            }
            if (surfaces.Count == 0)
            {
                editor.WriteMessage("\nКЗМ: в чертеже нет поверхностей.");
                return;
            }
            var chosen = AskSurfaceNumber(editor, surfaces, currentId, action == "Current" ? "\nНомер поверхности, которая станет текущей" : "\nНомер поверхности для переименования", false);
            if (chosen == null) return;
            if (action == "Current")
            {
                Save(database, chosen, false, true);
                editor.WriteMessage("\nКЗМ: текущая поверхность — " + chosen.Title + ".");
                return;
            }
            var name = editor.GetString(new PromptStringOptions("\nНовое имя поверхности «" + chosen.Name + "»: ") { AllowSpaces = true });
            if (name.Status != PromptStatus.OK) return;
            chosen.Name = SurfaceNames.Validate(name.StringResult);
            Save(database, chosen, true, false);
            editor.WriteMessage("\nКЗМ: поверхность переименована: " + chosen.Title + ".");
        });

        private static void Save(Database database, SurfaceInfo surface, bool write, bool makeCurrent)
        {
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                if (write)
                {
                    Layers.Ensure(database, transaction, surface.Layer);
                    KzmStore.WriteSurface(database, transaction, surface);
                }
                if (makeCurrent) KzmStore.WriteCurrentSurfaceId(database, transaction, surface.Id);
                transaction.Commit();
            }
        }

        internal static (List<SurfaceInfo> Surfaces, string CurrentId, Dictionary<string, int> Counts) ReadSurfaceList(Database database, KzmSettings settings)
        {
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var surfaces = KzmStore.ReadSurfaces(database, transaction);
                string current = KzmStore.ReadCurrentSurfaceId(database, transaction);
                var counts = PointBlocks.Read(database, transaction, settings)
                    .GroupBy(point => point.Data.SurfaceId).ToDictionary(group => group.Key, group => group.Count());
                transaction.Commit();
                return (surfaces, current, counts);
            }
        }

        internal static void PrintSurfaces(Editor editor, IReadOnlyList<SurfaceInfo> surfaces, string currentId, IReadOnlyDictionary<string, int> counts)
        {
            if (surfaces.Count == 0)
            {
                editor.WriteMessage("\nКЗМ: поверхностей в чертеже пока нет.");
                return;
            }
            editor.WriteMessage("\nПоверхности чертежа:");
            for (int i = 0; i < surfaces.Count; i++)
            {
                var surface = surfaces[i];
                counts.TryGetValue(surface.Id, out int count);
                editor.WriteMessage("\n  " + (i + 1).ToString(CultureInfo.InvariantCulture) + " — " + surface.Title +
                    ", точек: " + count + (surface.Id == currentId ? " — текущая" : string.Empty));
            }
        }

        /// <summary>Asks for a surface number; with <paramref name="allowNew"/> the «Новая» keyword returns a new, unsaved surface.</summary>
        internal static SurfaceInfo AskSurfaceNumber(Editor editor, IReadOnlyList<SurfaceInfo> surfaces, string currentId, string message, bool allowNew,
            Document document = null)
        {
            int current = Math.Max(0, surfaces.ToList().FindIndex(surface => surface.Id == currentId));
            var options = new PromptIntegerOptions(message)
            {
                AllowNone = true,
                LowerLimit = 1,
                UpperLimit = surfaces.Count,
                DefaultValue = current + 1,
                UseDefaultValue = true,
            };
            if (allowNew) options.Keywords.Add("New", "Новая", "Новая");
            var result = editor.GetInteger(options);
            if (result.Status == PromptStatus.Keyword && allowNew) return AskNewSurface(document, surfaces);
            if (result.Status == PromptStatus.None) return surfaces[current];
            if (result.Status != PromptStatus.OK) return null;
            return surfaces[result.Value - 1];
        }

        /// <summary>Name, type and points layer of a new surface (not yet written to the drawing).</summary>
        internal static SurfaceInfo AskNewSurface(Document document, IReadOnlyList<SurfaceInfo> existing)
        {
            var editor = document.Editor;
            string name;
            while (true)
            {
                var answer = editor.GetString(new PromptStringOptions("\nИмя новой поверхности: ") { AllowSpaces = true });
                if (answer.Status != PromptStatus.OK) return null;
                try
                {
                    name = SurfaceNames.Validate(answer.StringResult);
                }
                catch (KzmException exception)
                {
                    editor.WriteMessage("\n" + exception.Message);
                    continue;
                }
                if (existing.Any(surface => string.Equals(surface.Name, name, StringComparison.CurrentCultureIgnoreCase)))
                {
                    editor.WriteMessage("\nПоверхность «" + name + "» уже есть. Введите другое имя.");
                    continue;
                }
                break;
            }
            string kind = AskKeyword(editor, "\nТип поверхности", "Fact", ("Fact", "Фактическая"), ("Project", "Проектная"));
            if (kind == null) return null;
            var surfaceKind = kind == "Project" ? SurfaceKind.Project : SurfaceKind.Fact;
            string layer = AskLayer(document, SurfaceInfo.DefaultLayer(surfaceKind));
            if (layer == null) return null;
            return new SurfaceInfo
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name,
                Kind = surfaceKind,
                Layer = layer,
                Created = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                Revision = 0,
            };
        }

        /// <summary>Сначала слой: the layer that will receive the points of the surface.</summary>
        private static string AskLayer(Document document, string defaultLayer)
        {
            var editor = document.Editor;
            while (true)
            {
                var options = new PromptEntityOptions(string.Empty) { AllowNone = true };
                options.SetMessageAndKeywords(
                    "\nСлой для точек: укажите объект на нужном слое или [Имя] <" + defaultLayer + ">: ", "Name");
                var result = editor.GetEntity(options);
                if (result.Status == PromptStatus.None) return defaultLayer;
                if (result.Status == PromptStatus.Keyword)
                {
                    var typed = editor.GetString(new PromptStringOptions("\nИмя слоя: ") { AllowSpaces = true });
                    if (typed.Status != PromptStatus.OK) return null;
                    string layer = typed.StringResult.Trim();
                    try
                    {
                        SymbolUtilityServices.ValidateSymbolName(layer, false);
                        return layer;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        editor.WriteMessage("\nНедопустимое имя слоя: «" + layer + "».");
                        continue;
                    }
                }
                if (result.Status != PromptStatus.OK) return null;
                using (var transaction = document.Database.TransactionManager.StartOpenCloseTransaction())
                {
                    var entity = (Entity)transaction.GetObject(result.ObjectId, OpenMode.ForRead);
                    string layer = entity.Layer;
                    transaction.Commit();
                    editor.WriteMessage("\nСлой точек: " + layer);
                    return layer;
                }
            }
        }
    }
}
