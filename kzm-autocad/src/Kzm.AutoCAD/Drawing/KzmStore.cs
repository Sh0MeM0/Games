using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Kzm.Core;

namespace Kzm.AutoCAD.Drawing
{
    /// <summary>A pair of point blocks closer than the tolerance, waiting for the user's decision.</summary>
    internal sealed class StoredPair
    {
        internal StoredPair(ObjectId first, ObjectId second, double distance)
        {
            First = first;
            Second = second;
            Distance = distance;
        }

        internal ObjectId First { get; }
        internal ObjectId Second { get; }
        internal double Distance { get; }

        internal bool IsAlive =>
            First.IsValid && Second.IsValid && !First.IsErased && !Second.IsErased;

        internal bool Same(StoredPair other) =>
            (First == other.First && Second == other.Second) || (First == other.Second && Second == other.First);
    }

    /// <summary>
    /// All module data lives in the Named Objects Dictionary branch «SH0MEM0_KZM»
    /// (Xrecords). It is saved with the DWG and takes part in normal UNDO.
    /// Point blocks themselves carry their data in XData (see PointBlocks).
    /// </summary>
    internal static class KzmStore
    {
        internal const string RootName = "SH0MEM0_KZM";
        private const string SettingsName = "SETTINGS";
        private const string SurfacesName = "SURFACES";
        private const string CurrentName = "CURRENT_SURFACE";
        private const string ConflictsName = "DUPLICATE_PAIRS";
        private const string KeptName = "KEPT_PAIRS";
        private const int MaximumSurfaces = 256;

        // ---------- settings ----------

        internal static KzmSettings ReadSettings(Database database, Transaction transaction)
        {
            var root = Root(database, transaction, false);
            if (root == null || !root.Contains(SettingsName)) return new KzmSettings();
            return SettingsCodec.Decode(ReadPairs(root, SettingsName, transaction));
        }

        internal static void WriteSettings(Database database, Transaction transaction, KzmSettings settings)
        {
            // Never overwrite settings written by a newer module version.
            ReadSettings(database, transaction);
            WritePairs(Root(database, transaction, true), SettingsName, SettingsCodec.Encode(settings), transaction);
        }

        // ---------- surfaces ----------

        internal static List<SurfaceInfo> ReadSurfaces(Database database, Transaction transaction)
        {
            var result = new List<SurfaceInfo>();
            var root = Root(database, transaction, false);
            if (root == null || !root.Contains(SurfacesName)) return result;
            var surfaces = (DBDictionary)transaction.GetObject(root.GetAt(SurfacesName), OpenMode.ForRead);
            foreach (DBDictionaryEntry entry in surfaces)
            {
                var map = ReadPairs(surfaces, entry.Key, transaction).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                try
                {
                    result.Add(new SurfaceInfo
                    {
                        Id = entry.Key,
                        Name = map["name"],
                        Kind = SurfaceKinds.Parse(map["kind"]),
                        Layer = map["layer"],
                        Created = map.TryGetValue("created", out string created) ? created : string.Empty,
                        Revision = map.TryGetValue("revision", out string revision)
                            ? int.Parse(revision, NumberStyles.Integer, CultureInfo.InvariantCulture) : 0,
                    });
                }
                catch (Exception exception) when (exception is KeyNotFoundException || exception is FormatException || exception is KzmException)
                {
                    throw new KzmException("Запись поверхности " + entry.Key + " в чертеже повреждена: " + exception.Message, exception);
                }
            }
            return result.OrderBy(surface => surface.Kind).ThenBy(surface => surface.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        internal static void WriteSurface(Database database, Transaction transaction, SurfaceInfo surface)
        {
            if (string.IsNullOrEmpty(surface.Id)) throw new ArgumentException("Нет идентификатора поверхности.");
            surface.Name = SurfaceNames.Validate(surface.Name);
            var existing = ReadSurfaces(database, transaction);
            if (existing.Any(other => other.Id != surface.Id &&
                    string.Equals(other.Name, surface.Name, StringComparison.CurrentCultureIgnoreCase)))
                throw new KzmException("Поверхность с именем «" + surface.Name + "» уже есть в чертеже.");
            if (existing.All(other => other.Id != surface.Id) && existing.Count >= MaximumSurfaces)
                throw new KzmException("В чертеже уже " + MaximumSurfaces + " поверхностей.");
            var root = Root(database, transaction, true);
            var surfaces = Child(root, SurfacesName, transaction);
            WritePairs(surfaces, surface.Id, new[]
            {
                Pair("name", surface.Name),
                Pair("kind", SurfaceKinds.Code(surface.Kind)),
                Pair("layer", surface.Layer),
                Pair("created", surface.Created ?? string.Empty),
                Pair("revision", surface.Revision.ToString(CultureInfo.InvariantCulture)),
            }, transaction);
        }

        internal static void DeleteSurface(Database database, Transaction transaction, string surfaceId)
        {
            var root = Root(database, transaction, false);
            if (root == null) return;
            if (root.Contains(SurfacesName))
            {
                var surfaces = (DBDictionary)transaction.GetObject(root.GetAt(SurfacesName), OpenMode.ForWrite);
                if (surfaces.Contains(surfaceId))
                {
                    var record = transaction.GetObject(surfaces.GetAt(surfaceId), OpenMode.ForWrite);
                    surfaces.Remove(surfaceId);
                    record.Erase();
                }
            }
            if (ReadCurrentSurfaceId(database, transaction) == surfaceId)
            {
                var record = transaction.GetObject(root.GetAt(CurrentName), OpenMode.ForWrite);
                root.UpgradeOpen();
                root.Remove(CurrentName);
                record.Erase();
            }
        }

        internal static void TouchSurface(Database database, Transaction transaction, string surfaceId)
        {
            var surface = ReadSurfaces(database, transaction).FirstOrDefault(item => item.Id == surfaceId);
            if (surface == null) return;
            surface.Revision++;
            WriteSurface(database, transaction, surface);
        }

        internal static string ReadCurrentSurfaceId(Database database, Transaction transaction)
        {
            var root = Root(database, transaction, false);
            if (root == null || !root.Contains(CurrentName)) return null;
            return ReadPairs(root, CurrentName, transaction).Where(pair => pair.Key == "id").Select(pair => pair.Value).FirstOrDefault();
        }

        internal static void WriteCurrentSurfaceId(Database database, Transaction transaction, string surfaceId)
        {
            WritePairs(Root(database, transaction, true), CurrentName, new[] { Pair("id", surfaceId) }, transaction);
        }

        // ---------- duplicate pairs ----------

        internal static List<StoredPair> ReadConflicts(Database database, Transaction transaction) =>
            ReadPairList(database, transaction, ConflictsName);

        internal static void WriteConflicts(Database database, Transaction transaction, IEnumerable<StoredPair> pairs) =>
            WritePairList(database, transaction, ConflictsName, pairs);

        internal static List<StoredPair> ReadKept(Database database, Transaction transaction) =>
            ReadPairList(database, transaction, KeptName);

        internal static void WriteKept(Database database, Transaction transaction, IEnumerable<StoredPair> pairs) =>
            WritePairList(database, transaction, KeptName, pairs);

        private static List<StoredPair> ReadPairList(Database database, Transaction transaction, string name)
        {
            var result = new List<StoredPair>();
            var root = Root(database, transaction, false);
            if (root == null || !root.Contains(name)) return result;
            var record = (Xrecord)transaction.GetObject(root.GetAt(name), OpenMode.ForRead);
            var values = record.Data?.AsArray() ?? new TypedValue[0];
            for (int i = 0; i + 2 < values.Length; i += 3)
            {
                if (!(values[i].Value is ObjectId first) || !(values[i + 1].Value is ObjectId second)) continue;
                double distance = values[i + 2].Value is double d ? d : double.NaN;
                result.Add(new StoredPair(first, second, distance));
            }
            return result;
        }

        private static void WritePairList(Database database, Transaction transaction, string name, IEnumerable<StoredPair> pairs)
        {
            var root = Root(database, transaction, true);
            var data = new ResultBuffer();
            foreach (var pair in pairs.Where(pair => pair.IsAlive))
            {
                data.Add(new TypedValue((int)DxfCode.SoftPointerId, pair.First));
                data.Add(new TypedValue((int)DxfCode.SoftPointerId, pair.Second));
                data.Add(new TypedValue((int)DxfCode.Real, double.IsNaN(pair.Distance) ? 0.0 : pair.Distance));
            }
            WriteRecord(root, name, data, transaction);
        }

        // ---------- low level ----------

        private static DBDictionary Root(Database database, Transaction transaction, bool create)
        {
            var named = (DBDictionary)transaction.GetObject(database.NamedObjectsDictionaryId, OpenMode.ForRead);
            if (named.Contains(RootName))
            {
                var obj = transaction.GetObject(named.GetAt(RootName), OpenMode.ForRead);
                if (obj is DBDictionary existing) return existing;
                throw new KzmException("Запись " + RootName + " в чертеже имеет неожиданный тип; данные не изменены.");
            }
            if (!create) return null;
            named.UpgradeOpen();
            var root = new DBDictionary();
            named.SetAt(RootName, root);
            transaction.AddNewlyCreatedDBObject(root, true);
            return root;
        }

        private static DBDictionary Child(DBDictionary parent, string name, Transaction transaction)
        {
            if (parent.Contains(name))
            {
                var obj = transaction.GetObject(parent.GetAt(name), OpenMode.ForRead);
                if (obj is DBDictionary existing) return existing;
                throw new KzmException("Запись " + name + " в чертеже имеет неожиданный тип; данные не изменены.");
            }
            if (!parent.IsWriteEnabled) parent.UpgradeOpen();
            var child = new DBDictionary();
            parent.SetAt(name, child);
            transaction.AddNewlyCreatedDBObject(child, true);
            return child;
        }

        private static List<KeyValuePair<string, string>> ReadPairs(DBDictionary dictionary, string key, Transaction transaction)
        {
            var obj = transaction.GetObject(dictionary.GetAt(key), OpenMode.ForRead);
            if (!(obj is Xrecord record)) throw new KzmException("Запись " + key + " в чертеже имеет неожиданный тип.");
            var values = record.Data?.AsArray() ?? new TypedValue[0];
            var result = new List<KeyValuePair<string, string>>();
            for (int i = 0; i + 1 < values.Length; i += 2)
                result.Add(Pair(Convert.ToString(values[i].Value, CultureInfo.InvariantCulture),
                    Convert.ToString(values[i + 1].Value, CultureInfo.InvariantCulture)));
            return result;
        }

        private static void WritePairs(DBDictionary dictionary, string key, IEnumerable<KeyValuePair<string, string>> pairs, Transaction transaction)
        {
            var data = new ResultBuffer();
            foreach (var pair in pairs)
            {
                data.Add(new TypedValue((int)DxfCode.Text, pair.Key));
                data.Add(new TypedValue((int)DxfCode.Text, pair.Value ?? string.Empty));
            }
            WriteRecord(dictionary, key, data, transaction);
        }

        private static void WriteRecord(DBDictionary dictionary, string key, ResultBuffer data, Transaction transaction)
        {
            if (dictionary.Contains(key))
            {
                var obj = transaction.GetObject(dictionary.GetAt(key), OpenMode.ForWrite);
                if (!(obj is Xrecord existing)) throw new KzmException("Запись " + key + " в чертеже имеет неожиданный тип.");
                existing.Data = data;
                return;
            }
            if (!dictionary.IsWriteEnabled) dictionary.UpgradeOpen();
            var record = new Xrecord { Data = data };
            dictionary.SetAt(key, record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        private static KeyValuePair<string, string> Pair(string key, string value) => new KeyValuePair<string, string>(key, value);
    }
}
