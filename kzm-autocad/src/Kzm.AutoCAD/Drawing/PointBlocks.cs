using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Kzm.Core;

namespace Kzm.AutoCAD.Drawing
{
    /// <summary>Data carried by one point block (XData application «SH0MEM0_KZM»).</summary>
    internal sealed class PointData
    {
        internal string SurfaceId { get; set; }
        /// <summary>Exact elevation; the attribute shows it rounded.</summary>
        internal double Elevation { get; set; }
        internal SourceKind Source { get; set; }
        internal string SourceHandle { get; set; }
        internal XyMode XyMode { get; set; }
        internal string MarkerHandle { get; set; }
        /// <summary>Temporarily excluded from triangulation (stage 2).</summary>
        internal bool Excluded { get; set; }
    }

    /// <summary>A point block read from the drawing with its current elevation.</summary>
    internal sealed class SurfacePoint
    {
        internal ObjectId Id { get; set; }
        internal string Handle { get; set; }
        internal XY Position { get; set; }
        internal PointData Data { get; set; }
        internal ElevationState Elevation { get; set; }
    }

    /// <summary>
    /// Points are ordinary blocks «КЗМ_ТОЧКА»: MOVE, COPY, ERASE and grips work as
    /// for any block. The elevation is the «ОТМЕТКА» attribute (editable in the
    /// Properties palette); the exact value is also kept in XData. The block is
    /// inserted at Z = 0; its Z is not used.
    /// </summary>
    internal static class PointBlocks
    {
        internal const string BlockName = "КЗМ_ТОЧКА";
        internal const string Tag = "ОТМЕТКА";
        internal const string AppName = "SH0MEM0_KZM";
        private const string RecordMarker = "KZM_POINT";
        private const short RecordVersion = 1;

        internal static void EnsureRegApp(Database database, Transaction transaction, string name)
        {
            var table = (RegAppTable)transaction.GetObject(database.RegAppTableId, OpenMode.ForRead);
            if (table.Has(name)) return;
            table.UpgradeOpen();
            var record = new RegAppTableRecord { Name = name };
            table.Add(record);
            transaction.AddNewlyCreatedDBObject(record, true);
        }

        /// <summary>Creates the block definition, or updates its sizes to the settings.</summary>
        internal static ObjectId EnsureDefinition(Database database, Transaction transaction, KzmSettings settings)
        {
            double size = settings.PointSizeMm.Value;
            double height = settings.LabelHeightMm.Value;
            var blocks = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            BlockTableRecord definition;
            if (blocks.Has(BlockName))
            {
                definition = (BlockTableRecord)transaction.GetObject(blocks[BlockName], OpenMode.ForWrite);
            }
            else
            {
                blocks.UpgradeOpen();
                definition = new BlockTableRecord
                {
                    Name = BlockName,
                    Origin = Point3d.Origin,
                    Comments = "Точка поверхности модуля «Картограмма земляных масс». Размеры — мм на листе.",
                    Units = UnitsValue.Undefined,
                };
                blocks.Add(definition);
                transaction.AddNewlyCreatedDBObject(definition, true);
            }

            Circle circle = null;
            AttributeDefinition attribute = null;
            foreach (ObjectId id in definition)
            {
                var entity = transaction.GetObject(id, OpenMode.ForWrite);
                if (entity is Circle c && circle == null) circle = c;
                else if (entity is AttributeDefinition a && string.Equals(a.Tag, Tag, StringComparison.OrdinalIgnoreCase)) attribute = a;
            }
            if (circle == null)
            {
                circle = new Circle(Point3d.Origin, Vector3d.ZAxis, size / 2) { Layer = "0" };
                definition.AppendEntity(circle);
                transaction.AddNewlyCreatedDBObject(circle, true);
            }
            circle.Center = Point3d.Origin;
            circle.Radius = size / 2;

            if (attribute == null)
            {
                attribute = new AttributeDefinition
                {
                    Tag = Tag,
                    Prompt = "Отметка, м",
                    TextString = string.Empty,
                    Layer = "0",
                    Verifiable = false,
                    Preset = false,
                    Constant = false,
                    Invisible = false,
                };
                definition.AppendEntity(attribute);
                transaction.AddNewlyCreatedDBObject(attribute, true);
            }
            attribute.TextStyleId = database.Textstyle;
            attribute.Height = height;
            attribute.HorizontalMode = TextHorizontalMode.TextLeft;
            attribute.VerticalMode = TextVerticalMode.TextBase;
            attribute.Position = LabelPosition(size, height);
            return definition.ObjectId;
        }

        /// <summary>Label to the right of the sign, vertically centred (in paper mm).</summary>
        private static Point3d LabelPosition(double size, double height) => new Point3d(size / 2 + height / 2, -height / 2, 0);

        internal static ObjectId Create(Transaction transaction, BlockTableRecord space, ObjectId definitionId,
            XY position, string layer, PointData data, KzmSettings settings)
        {
            var definition = (BlockTableRecord)transaction.GetObject(definitionId, OpenMode.ForRead);
            double scale = settings.UnitsPerPaperMm;
            var reference = new BlockReference(new Point3d(position.X, position.Y, 0), definitionId)
            {
                Layer = layer,
                ScaleFactors = new Scale3d(scale),
            };
            space.AppendEntity(reference);
            transaction.AddNewlyCreatedDBObject(reference, true);
            foreach (ObjectId id in definition)
            {
                if (!(transaction.GetObject(id, OpenMode.ForRead) is AttributeDefinition attributeDefinition) || attributeDefinition.Constant)
                    continue;
                var attribute = new AttributeReference();
                attribute.SetAttributeFromBlock(attributeDefinition, reference.BlockTransform);
                if (string.Equals(attributeDefinition.Tag, Tag, StringComparison.OrdinalIgnoreCase))
                    attribute.TextString = ElevationFormat.Format(data.Elevation, settings.Decimals);
                reference.AttributeCollection.AppendAttribute(attribute);
                transaction.AddNewlyCreatedDBObject(attribute, true);
            }
            reference.XData = Encode(data);
            return reference.ObjectId;
        }

        internal static ResultBuffer Encode(PointData data)
        {
            var buffer = new ResultBuffer(
                new TypedValue((int)DxfCode.ExtendedDataRegAppName, AppName),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, RecordMarker),
                new TypedValue((int)DxfCode.ExtendedDataInteger16, RecordVersion),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, "surface"),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, data.SurfaceId),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, "z"),
                new TypedValue((int)DxfCode.ExtendedDataReal, data.Elevation),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, "src"),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, SourceKinds.Code(data.Source)),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, "xy"),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, XyModes.Code(data.XyMode)),
                new TypedValue((int)DxfCode.ExtendedDataAsciiString, "excl"),
                new TypedValue((int)DxfCode.ExtendedDataInteger16, (short)(data.Excluded ? 1 : 0)));
            if (!string.IsNullOrEmpty(data.SourceHandle))
            {
                buffer.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, "srch"));
                buffer.Add(new TypedValue((int)DxfCode.ExtendedDataHandle, data.SourceHandle));
            }
            if (!string.IsNullOrEmpty(data.MarkerHandle))
            {
                buffer.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, "mkh"));
                buffer.Add(new TypedValue((int)DxfCode.ExtendedDataHandle, data.MarkerHandle));
            }
            return buffer;
        }

        /// <summary>Returns null when the object is not a point of this module.</summary>
        internal static PointData Decode(DBObject obj)
        {
            ResultBuffer xdata = obj.GetXDataForApplication(AppName);
            if (xdata == null) return null;
            using (xdata)
            {
                var values = xdata.AsArray();
                if (values.Length < 3 || !Equals(values[1].Value, RecordMarker)) return null;
                var data = new PointData();
                bool hasSurface = false, hasZ = false;
                for (int i = 3; i + 1 < values.Length; i += 2)
                {
                    string key = values[i].Value as string;
                    object value = values[i + 1].Value;
                    switch (key)
                    {
                        case "surface": data.SurfaceId = value as string; hasSurface = !string.IsNullOrEmpty(data.SurfaceId); break;
                        case "z": if (value is double z) { data.Elevation = z; hasZ = true; } break;
                        case "src": data.Source = SourceKinds.Parse(value as string); break;
                        case "xy": data.XyMode = XyModes.Parse(value as string); break;
                        case "excl": data.Excluded = Convert.ToInt32(value) != 0; break;
                        case "srch": data.SourceHandle = Convert.ToString(value); break;
                        case "mkh": data.MarkerHandle = Convert.ToString(value); break;
                    }
                }
                return hasSurface && hasZ ? data : null;
            }
        }

        internal static string ReadLabel(BlockReference reference, Transaction transaction)
        {
            foreach (ObjectId id in reference.AttributeCollection)
            {
                var attribute = (AttributeReference)transaction.GetObject(id, OpenMode.ForRead);
                if (string.Equals(attribute.Tag, Tag, StringComparison.OrdinalIgnoreCase)) return attribute.TextString;
            }
            return null;
        }

        /// <summary>All live point blocks; when <paramref name="surfaceId"/> is set, only that surface.</summary>
        internal static List<SurfacePoint> Read(Database database, Transaction transaction, KzmSettings settings, string surfaceId = null)
        {
            var result = new List<SurfacePoint>();
            var blocks = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            if (!blocks.Has(BlockName)) return result;
            var definition = (BlockTableRecord)transaction.GetObject(blocks[BlockName], OpenMode.ForRead);
            foreach (ObjectId id in definition.GetBlockReferenceIds(true, false))
            {
                if (id.IsErased) continue;
                if (!(transaction.GetObject(id, OpenMode.ForRead) is BlockReference reference)) continue;
                // Only points placed in model or paper space, not nested in other blocks.
                if (!(transaction.GetObject(reference.OwnerId, OpenMode.ForRead) is BlockTableRecord owner) || !owner.IsLayout) continue;
                var data = Decode(reference);
                if (data == null) continue;
                if (surfaceId != null && !string.Equals(data.SurfaceId, surfaceId, StringComparison.Ordinal)) continue;
                result.Add(new SurfacePoint
                {
                    Id = id,
                    Handle = reference.Handle.ToString(),
                    Position = new XY(reference.Position.X, reference.Position.Y),
                    Data = data,
                    Elevation = PointElevation.Resolve(data.Elevation, ReadLabel(reference, transaction), settings.Decimals),
                });
            }
            return result;
        }

        /// <summary>
        /// Brings block sizes, scale and the number of decimals of all points in line
        /// with the settings. Elevations are resolved with the previous decimals first,
        /// so a value typed by the user into the attribute is kept.
        /// </summary>
        internal static int ApplySizes(Database database, Transaction transaction, KzmSettings settings, int previousDecimals)
        {
            var definitionId = EnsureDefinition(database, transaction, settings);
            var definition = (BlockTableRecord)transaction.GetObject(definitionId, OpenMode.ForRead);
            var attributeDefinitions = definition.Cast<ObjectId>()
                .Select(id => transaction.GetObject(id, OpenMode.ForRead))
                .OfType<AttributeDefinition>()
                .ToDictionary(item => item.Tag.ToUpperInvariant(), item => item);
            double scale = settings.UnitsPerPaperMm;
            int count = 0;
            foreach (ObjectId id in definition.GetBlockReferenceIds(true, false))
            {
                if (id.IsErased) continue;
                var reference = (BlockReference)transaction.GetObject(id, OpenMode.ForWrite);
                var data = Decode(reference);
                ElevationState state = data == null ? null
                    : PointElevation.Resolve(data.Elevation, ReadLabel(reference, transaction), previousDecimals);
                reference.ScaleFactors = new Scale3d(scale);
                foreach (ObjectId attributeId in reference.AttributeCollection)
                {
                    var attribute = (AttributeReference)transaction.GetObject(attributeId, OpenMode.ForWrite);
                    if (!attributeDefinitions.TryGetValue(attribute.Tag.ToUpperInvariant(), out var attributeDefinition)) continue;
                    string text = attribute.TextString;
                    attribute.SetAttributeFromBlock(attributeDefinition, reference.BlockTransform);
                    bool elevationLabel = string.Equals(attribute.Tag, Tag, StringComparison.OrdinalIgnoreCase);
                    attribute.TextString = elevationLabel && state != null && state.IsValid
                        ? ElevationFormat.Format(state.Value.Value, settings.Decimals)
                        : text;
                }
                if (data != null && state != null && state.IsValid && state.EditedByUser)
                {
                    data.Elevation = state.Value.Value;
                    reference.XData = Encode(data);
                }
                count++;
            }
            return count;
        }
    }
}
