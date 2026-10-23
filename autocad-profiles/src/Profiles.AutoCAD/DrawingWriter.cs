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

        private sealed class OutputWork
        {
            internal string ProfileId;
            internal string ProfileName;
            internal string SectionId;
            internal DrawingPlan Plan;
            internal double OffsetY;
            internal double DashScale;
            internal Matrix3d Transform;
            internal ObjectId Destination;
            internal StoredProfileOutput Output;
        }

        internal static InsertResult Insert(Database database, Editor editor,
            Point3d? insertionPoint, DrawingPlan plan, ProfileProject project, bool rebuildExisting,
            string selectedSectionId = null, bool allSections = false, bool allProfiles = false)
        {
            if (plan == null || plan.Entities.Count == 0)
                throw new ProfileValidationException("Нет объектов для построения профиля.");
            // Structural validation covers the full saved draft. Only the chosen
            // sections need complete drawing geometry.
            string serialized = ProjectFile.Serialize(project);
            bool cross = project.Kind == ProfileKind.CrossSection;
            var work = ProfileBuildScope.Groups(project, selectedSectionId, allSections, allProfiles).Select(group => new OutputWork
            {
                ProfileId = group.ProfileId, ProfileName = group.ProfileName, SectionId = group.SectionId,
                Plan = group.Plan, OffsetY = group.OffsetY,
                DashScale = group.Settings.TextHeightMm * group.Settings.HorizontalScale /
                    (1000 * group.Settings.MetresPerDrawingUnit)
            }).ToList();
            var result = new InsertResult();
            if (database.Ltscale > 0 && !double.IsNaN(database.Ltscale) && !double.IsInfinity(database.Ltscale))
                foreach (var item in work) item.DashScale /= database.Ltscale;

            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var previous = DrawingProjectStore.ReadOutputs(database, transaction, project.ProjectId, project.Kind);
                var legacy = cross ? previous.SingleOrDefault(output => output.Version == 1 && SameProfile(output, project.ProfileId)) : null;
                var removals = previous.Where(output => OutputMetadata.InScope(output, project.ProjectId, project.ProfileId,
                    project.Kind, selectedSectionId, allSections, allProfiles)).ToList();
                Matrix3d newBase = Matrix3d.Identity;
                if (!rebuildExisting)
                {
                    if (!insertionPoint.HasValue) throw new ProfileValidationException("Не выбрана точка вставки профиля.");
                    var ucsMatrix = editor.CurrentUserCoordinateSystem;
                    var ucs = ucsMatrix.CoordinateSystem3d;
                    // GetPoint returns UCS coordinates; the saved transform is WCS.
                    var insertionWcs = insertionPoint.Value.TransformBy(ucsMatrix);
                    newBase = Matrix3d.AlignCoordinateSystem(Point3d.Origin,
                        Vector3d.XAxis, Vector3d.YAxis, Vector3d.ZAxis,
                        insertionWcs, ucs.Xaxis, ucs.Yaxis, ucs.Zaxis);
                }

                // Resolve every placement before creating or erasing any entity.
                // In particular, an all-rebuild cannot silently place a new,
                // never-built section at another section's saved insertion point.
                foreach (var item in work)
                {
                    var old = OutputMetadata.FindPrevious(previous, project.ProjectId, project.Kind, item.ProfileId, item.SectionId);
                    if (rebuildExisting)
                    {
                        if (old != null)
                        {
                            item.Transform = new Matrix3d(old.Transform);
                            item.Destination = SavedSpace(database, transaction, old);
                        }
                        else if (cross && allSections && legacy != null)
                        {
                            item.Transform = new Matrix3d(legacy.Transform) * Matrix3d.Displacement(new Vector3d(0, item.OffsetY, 0));
                            item.Destination = SavedSpace(database, transaction, legacy);
                        }
                        else if (cross && legacy != null)
                            throw new ProfileValidationException("Для старого построения один раз выберите «Все поперечники» → «Перестроить все». Старое построение не изменено.");
                        else
                        {
                            string name = cross ? item.ProfileName + ": " + project.Sections.Single(section => string.Equals(section.SectionId, item.SectionId, StringComparison.OrdinalIgnoreCase)).Name : item.ProfileName;
                            throw new ProfileValidationException("Для «" + name + "» ещё нет сохранённого построения. Сначала используйте «Построить текущий» или «Построить все» для выбранного элемента или всей группы. Существующие объекты не изменены.");
                        }
                    }
                    else
                    {
                        item.Transform = newBase * Matrix3d.Displacement(new Vector3d(0, item.OffsetY, 0));
                        item.Destination = database.CurrentSpaceId;
                    }
                    item.Output = new StoredProfileOutput
                    {
                        Version = 2, ProfileId = item.ProfileId, SectionId = item.SectionId,
                        ProjectId = project.ProjectId, Kind = DrawingProjectStore.KindCode(project.Kind),
                        DrawingId = DrawingProjectStore.DrawingId(database, transaction),
                        Fingerprint = DrawingProjectStore.Fingerprint(database),
                        GenerationId = Guid.NewGuid().ToString("N"),
                        Transform = item.Transform.ToArray(), SpaceHandle = item.Destination.Handle.ToString()
                    };
                }
                if (cross && !allSections && legacy != null)
                    result.Warnings.Add("Старое общее построение сохранено целиком: его объекты нельзя безопасно отнести к отдельным поперечникам. Для перехода выберите «Все поперечники» → «Перестроить все».");

                var layers = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
                var layerIds = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
                var dashedId = work.Any(item => item.Plan.Entities.Any(entity => entity.Dashed))
                    ? DashedLinetype(database, transaction) : ObjectId.Null;
                RegisterOutputApplication(database, transaction);
                foreach (var item in work)
                    AppendPlan(database, transaction, item, layers, layerIds, dashedId, item.DashScale, result);

                // One atomic transaction covers every new section, all verified
                // old geometry, migration and per-section placement records.
                foreach (var old in removals)
                {
                    ErasePrevious(database, transaction, old, result);
                    DrawingProjectStore.RemoveOutput(database, transaction, old, project.Kind);
                }
                DrawingProjectStore.Save(database, transaction, project, serialized);
                DrawingProjectStore.SaveOutputs(database, transaction, work.Select(item => item.Output), project.Kind);
                transaction.Commit();
            }
            return result;
        }

        private static bool SameProfile(StoredProfileOutput output, string profileId) =>
            string.Equals(OutputMetadata.OutputProfileId(output), profileId, StringComparison.OrdinalIgnoreCase);

        private static ObjectId SavedSpace(Database database, Transaction transaction, StoredProfileOutput output)
        {
            ObjectId id = FindObject(database, output.SpaceHandle);
            if (id.IsNull || !(transaction.GetObject(id, OpenMode.ForRead) is BlockTableRecord))
                throw new ProfileValidationException("Исходное пространство профиля удалено. Используйте «Построить текущий» или «Построить все» для новой точки вставки.");
            return id;
        }

        private static void AppendPlan(Database database, Transaction transaction, OutputWork work,
            LayerTable layers, Dictionary<string, ObjectId> layerIds, ObjectId dashedId, double dashScale, InsertResult result)
        {
            var space = (BlockTableRecord)transaction.GetObject(work.Destination, OpenMode.ForWrite);
            if (space.IsFromExternalReference)
                throw new ProfileValidationException("Нельзя заменить профиль во внешней ссылке. Выберите пространство текущего чертежа.");
            foreach (var item in work.Plan.Entities)
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
                    space.AppendEntity(hatch);
                    transaction.AddNewlyCreatedDBObject(hatch, true);
                    DrawingHatch.Complete(hatch, region, work.Transform);
                }
                else
                {
                    entity.TransformBy(work.Transform);
                    space.AppendEntity(entity);
                    transaction.AddNewlyCreatedDBObject(entity, true);
                }
                if (entity is Dimension dimension) dimension.RecomputeDimensionBlock(true);
                Tag(entity, work.Output);
                if (entity is DBText text) text.AdjustAlignment(database);
                work.Output.Handles.Add(entity.Handle.ToString());
                result.EntityCount++;
            }
        }

        private static ObjectId FindObject(Database database, string handle)
        {
            try
            {
                ObjectId id = database.GetObjectId(false, new Handle(OutputMetadata.HandleValue(handle)), 0);
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
            var fields = new List<TypedValue>
            {
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, OutputRegApp),
                new TypedValue((int)DxfCode.ExtendedDataInteger16, (short)output.Version),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, output.DrawingId),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, output.ProjectId),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, output.Kind),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, output.GenerationId),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, output.Fingerprint)
            };
            if (output.Version == 2)
            {
                fields.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, output.ProfileId));
                fields.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, output.SectionId ?? ""));
            }
            using (var values = new ResultBuffer(fields.ToArray()))
                entity.XData = values;
        }

        private static bool OwnedBy(Entity entity, StoredProfileOutput output)
        {
            using (var data = entity.GetXDataForApplication(OutputRegApp))
            {
                if (data == null) return false;
                var values = data.AsArray();
                if (values.Length != (output.Version == 1 ? 7 : 9) || values[0].TypeCode != (int)DxfCode.ExtendedDataRegAppName ||
                    !string.Equals(values[0].Value as string, OutputRegApp, StringComparison.Ordinal) ||
                    values[1].TypeCode != (int)DxfCode.ExtendedDataInteger16 || Convert.ToInt32(values[1].Value, CultureInfo.InvariantCulture) != output.Version)
                    return false;
                string[] expected = output.Version == 1
                    ? new[] { output.DrawingId, output.ProjectId, output.Kind, output.GenerationId, output.Fingerprint }
                    : new[] { output.DrawingId, output.ProjectId, output.Kind, output.GenerationId, output.Fingerprint, output.ProfileId, output.SectionId ?? "" };
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
            if (item is DrawDimension dimension) return DrawingDimension.Create(dimension, database);
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
