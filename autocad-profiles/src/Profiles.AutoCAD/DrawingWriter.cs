using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Profiles.Core;
using TextAlignment = Profiles.Core.TextAlignment;

namespace Profiles.AutoCAD
{
    internal sealed class InsertResult
    {
        public int EntityCount { get; set; }
        public int ReplacedEntityCount { get; set; }
        public List<string> Warnings { get; } = new List<string>();
    }

    internal static class DrawingWriter
    {
        private const string OutputRegApp = "SH0MEM0_PROFILE_OUTPUT";

        internal static InsertResult Insert(Database database, Editor editor,
            Point3d? insertionPoint, DrawingPlan plan, ProfileProject project, bool rebuildExisting)
        {
            if (plan == null || plan.Entities.Count == 0)
                throw new ProfileValidationException("Нет объектов для построения профиля.");
            // The full plan and draft must be valid before opening/erasing anything.
            string serialized = ProjectFile.Serialize(project);
            var result = new InsertResult();
            // Fitted labels in narrow cells may be much smaller than the base
            // text. They must not shrink all water/cleaned-profile dash lengths.
            var effectiveSettings = ProfileScales.ForKind(project.Settings, project.Kind);
            var dashScale = effectiveSettings.TextHeightMm * effectiveSettings.HorizontalScale /
                (1000 * effectiveSettings.MetresPerDrawingUnit);
            // Keep the intended local dash length without changing global LTSCALE.
            if (database.Ltscale > 0 && !double.IsNaN(database.Ltscale) && !double.IsInfinity(database.Ltscale))
                dashScale /= database.Ltscale;

            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var previous = DrawingProjectStore.ReadOutput(database, transaction, project.ProjectId, project.Kind);
                if (rebuildExisting && previous == null)
                    throw new ProfileValidationException("Для этого проекта и типа ещё нет сохранённого построения. Используйте «Построить / переместить» и укажите точку вставки. Профили версии 0.1.x автоматически не заменяются.");
                Matrix3d transform;
                ObjectId destination;
                if (rebuildExisting)
                {
                    transform = new Matrix3d(previous.Transform);
                    destination = FindObject(database, previous.SpaceHandle);
                    if (destination.IsNull || !(transaction.GetObject(destination, OpenMode.ForRead) is BlockTableRecord))
                        throw new ProfileValidationException("Исходное пространство профиля удалено. Используйте «Построить / переместить» для новой точки вставки.");
                }
                else
                {
                    if (!insertionPoint.HasValue) throw new ProfileValidationException("Не выбрана точка вставки профиля.");
                    var ucsMatrix = editor.CurrentUserCoordinateSystem;
                    var ucs = ucsMatrix.CoordinateSystem3d;
                    // Editor.GetPoint returns UCS coordinates: Autodesk MgdDbg's
                    // MakeEntTests.Point converts the result before storing it.
                    var insertionWcs = insertionPoint.Value.TransformBy(ucsMatrix);
                    transform = Matrix3d.AlignCoordinateSystem(Point3d.Origin,
                        Vector3d.XAxis, Vector3d.YAxis, Vector3d.ZAxis,
                        insertionWcs, ucs.Xaxis, ucs.Yaxis, ucs.Zaxis);
                    destination = database.CurrentSpaceId;
                }
                var output = new StoredProfileOutput
                {
                    ProjectId = project.ProjectId,
                    Kind = DrawingProjectStore.KindCode(project.Kind),
                    DrawingId = DrawingProjectStore.DrawingId(database, transaction),
                    Fingerprint = DrawingProjectStore.Fingerprint(database),
                    GenerationId = Guid.NewGuid().ToString("N"),
                    Transform = transform.ToArray(),
                    SpaceHandle = destination.Handle.ToString()
                };
                var space = (BlockTableRecord)transaction.GetObject(
                    destination, OpenMode.ForWrite);
                if (space.IsFromExternalReference)
                    throw new ProfileValidationException("Нельзя заменить профиль во внешней ссылке. Выберите пространство текущего чертежа.");
                var layers = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
                var layerIds = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
                var dashedId = plan.Entities.Any(entity => entity.Dashed)
                    ? DashedLinetype(database, transaction) : ObjectId.Null;
                RegisterOutputApplication(database, transaction);

                foreach (var item in plan.Entities)
                {
                    if (string.IsNullOrWhiteSpace(item.Layer) || !item.Layer.StartsWith("PROF_", StringComparison.Ordinal))
                        throw new InvalidOperationException("Некорректный слой профиля.");
                    if (!layerIds.TryGetValue(item.Layer, out var layerId))
                    {
                        layerId = GetLayer(item.Layer, layers, transaction, result.Warnings);
                        layerIds.Add(item.Layer, layerId);
                    }

                    var entity = CreateEntity(item, database);
                    if (entity == null) continue;
                    entity.LayerId = layerId;
                    entity.Color = Color.FromColorIndex(ColorMethod.ByAci, item.ColorIndex);
                    entity.LineWeight = GetLineWeight(item.LineWeightMm);
                    entity.LinetypeId = database.ContinuousLinetype;
                    entity.LinetypeScale = 1;
                    if (item.Dashed)
                    {
                        entity.LinetypeId = dashedId;
                        entity.LinetypeScale = dashScale;
                        if (entity is Polyline dashedPolyline) dashedPolyline.Plinegen = true;
                    }
                    if (entity is Hatch hatch && item is DrawHatch region)
                    {
                        // AutoCAD requires a database-resident hatch before its
                        // loop/pattern are evaluated. It remains in the same
                        // transaction as all new geometry and prior erasure.
                        space.AppendEntity(hatch);
                        transaction.AddNewlyCreatedDBObject(hatch, true);
                        DrawingHatch.Complete(hatch, region, transform);
                    }
                    else
                    {
                        entity.TransformBy(transform);
                        space.AppendEntity(entity);
                        transaction.AddNewlyCreatedDBObject(entity, true);
                    }
                    Tag(entity, output);
                    if (entity is DBText text) text.AdjustAlignment(database);
                    output.Handles.Add(entity.Handle.ToString());
                    result.EntityCount++;
                }
                // Only after every new entity was created successfully do we erase
                // verified objects from the previous generation. Any failure rolls
                // back both geometry and records, preserving the prior output.
                if (previous != null) ErasePrevious(database, transaction, previous, result);
                DrawingProjectStore.Save(database, transaction, project, serialized);
                DrawingProjectStore.SaveOutput(database, transaction, output, project.Kind);
                transaction.Commit();
            }
            return result;
        }

        private static ObjectId FindObject(Database database, string handle)
        {
            try
            {
                ObjectId id = database.GetObjectId(false, new Handle(DrawingProjectStore.HandleValue(handle)), 0);
                return id.IsNull || !id.IsValid || id.IsErased || id.Database != database ? ObjectId.Null : id;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception exception)
                when (exception.ErrorStatus.ToString() == "UnknownHandle" || exception.ErrorStatus.ToString() == "KeyNotFound")
            { return ObjectId.Null; }
        }

        private static void RegisterOutputApplication(Database database, Transaction transaction)
        {
            var applications = (RegAppTable)transaction.GetObject(database.RegAppTableId, OpenMode.ForRead);
            if (applications.Has(OutputRegApp)) return;
            applications.UpgradeOpen();
            var record = new RegAppTableRecord { Name = OutputRegApp };
            applications.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        private static void Tag(Entity entity, StoredProfileOutput output)
        {
            using (var values = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, OutputRegApp),
                new TypedValue((int)DxfCode.ExtendedDataInteger16, (short)1),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, output.DrawingId),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, output.ProjectId),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, output.Kind),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, output.GenerationId),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, output.Fingerprint)))
                entity.XData = values;
        }

        private static bool OwnedBy(Entity entity, StoredProfileOutput output)
        {
            using (var data = entity.GetXDataForApplication(OutputRegApp))
            {
                if (data == null) return false;
                var values = data.AsArray();
                if (values.Length != 7 || values[0].TypeCode != (int)DxfCode.ExtendedDataRegAppName ||
                    !string.Equals(values[0].Value as string, OutputRegApp, StringComparison.Ordinal) ||
                    values[1].TypeCode != (int)DxfCode.ExtendedDataInteger16 || Convert.ToInt32(values[1].Value, CultureInfo.InvariantCulture) != 1)
                    return false;
                string[] expected = { output.DrawingId, output.ProjectId, output.Kind, output.GenerationId, output.Fingerprint };
                for (int i = 0; i < expected.Length; i++)
                    if (values[i + 2].TypeCode != (int)DxfCode.ExtendedDataAsciiString ||
                        !string.Equals(values[i + 2].Value as string, expected[i], StringComparison.Ordinal)) return false;
                return true;
            }
        }

        private static void ErasePrevious(Database database, Transaction transaction, StoredProfileOutput previous, InsertResult result)
        {
            ObjectId originalSpace = FindObject(database, previous.SpaceHandle);
            int missing = 0, retained = 0;
            foreach (string handle in previous.Handles)
            {
                ObjectId id = FindObject(database, handle);
                if (id.IsNull) { missing++; continue; }
                var entity = transaction.GetObject(id, OpenMode.ForRead) as Entity;
                if (entity == null || originalSpace.IsNull || entity.OwnerId != originalSpace || !OwnedBy(entity, previous))
                { retained++; continue; }
                var layer = (LayerTableRecord)transaction.GetObject(entity.LayerId, OpenMode.ForRead);
                if (layer.IsLocked)
                    throw new ProfileValidationException("Слой прежнего профиля «" + layer.Name + "» заблокирован. Разблокируйте его перед перестроением. Старое построение сохранено.");
                entity.UpgradeOpen();
                entity.Erase();
                result.ReplacedEntityCount++;
            }
            if (retained > 0)
                result.Warnings.Add("Сохранены объекты с изменённой принадлежностью или пространством: " + retained + ". Заменяются только проверенные объекты этого проекта.");
            if (missing > 0)
                result.Warnings.Add("Ранее удалённых объектов профиля: " + missing + ". Новое построение восстановлено по данным проекта.");
        }

        private static Entity CreateEntity(DrawEntity item, Database database)
        {
            if (item is DrawHatch)
            {
                var hatch = new Hatch();
                hatch.SetDatabaseDefaults(database);
                return hatch;
            }
            if (item is DrawPolyline line)
            {
                if (line.Points.Count < 2) return null;
                var polyline = new Polyline(line.Points.Count);
                polyline.SetDatabaseDefaults(database);
                for (var i = 0; i < line.Points.Count; i++)
                    polyline.AddVertexAt(i, new Point2d(line.Points[i].X, line.Points[i].Y), 0, 0, 0);
                polyline.Closed = line.Closed;
                return polyline;
            }
            if (item is DrawCircle circle)
            {
                var entity = new Circle();
                entity.SetDatabaseDefaults(database);
                entity.Center = new Point3d(circle.Center.X, circle.Center.Y, 0);
                entity.Normal = Vector3d.ZAxis;
                entity.Radius = circle.Radius;
                return entity;
            }
            if (item is DrawText label)
            {
                var position = new Point3d(label.Position.X, label.Position.Y, 0);
                var value = label.Text ?? string.Empty;
                if (value.Contains("\n") || label.MaxWidth > 0)
                {
                    var multiline = new MText();
                    multiline.SetDatabaseDefaults(database);
                    multiline.TextStyleId = database.Textstyle;
                    multiline.TextHeight = label.Height;
                    multiline.Width = Math.Max(0, label.MaxWidth);
                    multiline.Rotation = label.RotationDegrees * Math.PI / 180;
                    multiline.Attachment = label.Alignment == TextAlignment.Center ? AttachmentPoint.MiddleCenter :
                        label.Alignment == TextAlignment.Right ? AttachmentPoint.MiddleRight : AttachmentPoint.MiddleLeft;
                    multiline.Location = position;
                    var paragraphAlignment = label.Alignment == TextAlignment.Center ? "c" :
                        label.Alignment == TextAlignment.Right ? "r" : "l";
                    multiline.Contents = "{\\W" + label.WidthFactor.ToString("0.########", CultureInfo.InvariantCulture) +
                        ";\\pxq" + paragraphAlignment + ";" + EscapeMText(value) + "}";
                    return multiline;
                }
                var text = new DBText();
                text.SetDatabaseDefaults(database);
                text.Position = position;
                text.TextString = value;
                text.TextStyleId = database.Textstyle;
                text.Height = label.Height;
                text.WidthFactor = label.WidthFactor;
                text.Rotation = label.RotationDegrees * Math.PI / 180;
                text.HorizontalMode = label.Alignment == TextAlignment.Center ? TextHorizontalMode.TextCenter :
                    label.Alignment == TextAlignment.Right ? TextHorizontalMode.TextRight : TextHorizontalMode.TextLeft;
                text.VerticalMode = TextVerticalMode.TextVerticalMid;
                text.AlignmentPoint = position;
                return text;
            }
            throw new InvalidOperationException("Неизвестный элемент профиля.");
        }

        private static string EscapeMText(string value) => value.Replace("\\", "\\\\")
            .Replace("{", "\\{").Replace("}", "\\}").Replace("\r\n", "\n")
            .Replace("\r", "\n").Replace("\n", "\\P");

        private static ObjectId GetLayer(string requested, LayerTable layers,
            Transaction transaction, List<string> warnings)
        {
            var name = requested;
            for (var suffix = 2; layers.Has(name); suffix++)
            {
                var existing = (LayerTableRecord)transaction.GetObject(layers[name], OpenMode.ForRead);
                if (!existing.IsLocked && !existing.IsOff && !existing.IsFrozen)
                {
                    if (name != requested)
                        warnings.Add("Слой " + requested + " выключен, заморожен или заблокирован; использован " + name + ".");
                    return existing.ObjectId;
                }
                name = requested + "_" + suffix.ToString(CultureInfo.InvariantCulture);
            }
            if (!layers.IsWriteEnabled) layers.UpgradeOpen();
            var layer = new LayerTableRecord { Name = name };
            var id = layers.Add(layer);
            transaction.AddNewlyCreatedDBObject(layer, true);
            if (name != requested)
                warnings.Add("Слой " + requested + " выключен, заморожен или заблокирован; создан " + name + ".");
            return id;
        }

        private static ObjectId DashedLinetype(Database database, Transaction transaction)
        {
            var table = (LinetypeTable)transaction.GetObject(database.LinetypeTableId, OpenMode.ForRead);
            var name = "PROF_DASHED";
            for (var suffix = 2; table.Has(name); suffix++)
            {
                var existing = (LinetypeTableRecord)transaction.GetObject(table[name], OpenMode.ForRead);
                if (existing.NumDashes == 2 && Math.Abs(existing.PatternLength - 2) < 1e-9 &&
                    Math.Abs(existing.DashLengthAt(0) - 1.2) < 1e-9 &&
                    Math.Abs(existing.DashLengthAt(1) + 0.8) < 1e-9)
                    return existing.ObjectId;
                name = "PROF_DASHED_" + suffix.ToString(CultureInfo.InvariantCulture);
            }
            table.UpgradeOpen();
            var pattern = new LinetypeTableRecord
            {
                Name = name,
                AsciiDescription = "Профиля: штриховая линия",
                PatternLength = 2,
                NumDashes = 2
            };
            pattern.SetDashLengthAt(0, 1.2);
            pattern.SetDashLengthAt(1, -0.8);
            var id = table.Add(pattern);
            transaction.AddNewlyCreatedDBObject(pattern, true);
            return id;
        }

        private static LineWeight GetLineWeight(double millimetres)
        {
            var weights = new[] { 0, 5, 9, 13, 15, 18, 20, 25, 30, 35, 40, 50, 53, 60, 70, 80, 90, 100, 106, 120, 140, 158, 200, 211 };
            var desired = Math.Max(0, millimetres * 100);
            return (LineWeight)weights.OrderBy(weight => Math.Abs(weight - desired)).First();
        }
    }
}
