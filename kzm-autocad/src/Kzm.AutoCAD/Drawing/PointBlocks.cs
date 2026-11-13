using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.Colors;
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
        internal string Number { get; set; }
        /// <summary>Temporarily excluded from triangulation.</summary>
        internal bool Excluded { get; set; }
        /// <summary>No visible label (a point written from a label of the cartogram: the cartogram shows the value).</summary>
        internal bool HideLabel { get; set; }
    }

    /// <summary>A point block read from the drawing with its current elevation and number.</summary>
    internal sealed class SurfacePoint
    {
        internal ObjectId Id { get; set; }
        internal string Handle { get; set; }
        internal XY Position { get; set; }
        internal PointData Data { get; set; }
        internal ElevationState Elevation { get; set; }
        /// <summary>Number as shown in the «НОМЕР» attribute (edited by the user) or stored.</summary>
        internal string Number { get; set; }
    }

    /// <summary>
    /// Points are ordinary blocks «КЗМ_ТОЧКА»: MOVE, COPY, ERASE and grips work as for any
    /// block. The sign is a circle of diameter 1 scaled to the sign size of the surface; the
    /// attributes «ОТМЕТКА» and «НОМЕР» get the text style, height and colour of the surface
    /// (taken from a sample text). The exact elevation is also kept in XData. Z = 0.
    /// </summary>
    internal static class PointBlocks
    {
        internal const string BlockName = "КЗМ_ТОЧКА";
        internal const string Tag = "ОТМЕТКА";
        internal const string NumberTag = "НОМЕР";
        internal const string AppName = "SH0MEM0_KZM";
        /// <summary>Grey (ACI 8) for points excluded from the triangulation.</summary>
        internal const short ExcludedColor = 8;
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

        /// <summary>Unit block: circle of diameter 1, attributes «НОМЕР» and «ОТМЕТКА».</summary>
        internal static ObjectId EnsureDefinition(Database database, Transaction transaction)
        {
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
                    Comments = "Точка поверхности модуля «Картограмма земляных масс».",
                    Units = UnitsValue.Undefined,
                };
                blocks.Add(definition);
                transaction.AddNewlyCreatedDBObject(definition, true);
            }

            Circle circle = null;
            var attributes = new Dictionary<string, AttributeDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId id in definition)
            {
                var entity = transaction.GetObject(id, OpenMode.ForWrite);
                if (entity is Circle c && circle == null) circle = c;
                else if (entity is AttributeDefinition a) attributes[a.Tag] = a;
            }
            if (circle == null)
            {
                circle = new Circle(Point3d.Origin, Vector3d.ZAxis, 0.5) { Layer = "0" };
                definition.AppendEntity(circle);
                transaction.AddNewlyCreatedDBObject(circle, true);
            }
            circle.Center = Point3d.Origin;
            circle.Radius = 0.5;
            // ByBlock: the sign takes the colour of the point (grey for points excluded from the triangulation).
            if (!circle.Color.IsByBlock) circle.Color = Color.FromColorIndex(ColorMethod.ByBlock, 0);
            foreach (var (tag, prompt, y) in new[] { (NumberTag, "Номер точки", 0.6), (Tag, "Отметка, м", -0.5) })
            {
                if (!attributes.TryGetValue(tag, out var attribute))
                {
                    attribute = new AttributeDefinition { Tag = tag, Prompt = prompt, TextString = string.Empty, Layer = "0" };
                    definition.AppendEntity(attribute);
                    transaction.AddNewlyCreatedDBObject(attribute, true);
                }
                attribute.TextStyleId = database.Textstyle;
                attribute.Height = 1;
                attribute.HorizontalMode = TextHorizontalMode.TextLeft;
                attribute.VerticalMode = TextVerticalMode.TextBase;
                attribute.Position = new Point3d(0.75, y, 0);
            }
            return definition.ObjectId;
        }

        internal static ObjectId Create(Transaction transaction, BlockTableRecord space, ObjectId definitionId,
            XY position, string layer, PointData data, PointAppearance appearance, int decimals)
        {
            var reference = new BlockReference(new Point3d(position.X, position.Y, 0), definitionId) { Layer = layer };
            space.AppendEntity(reference);
            transaction.AddNewlyCreatedDBObject(reference, true);
            reference.XData = Encode(data);
            Apply(transaction, reference, data, appearance, decimals, true);
            return reference.ObjectId;
        }

        /// <summary>
        /// Brings one point in line with the appearance of its surface: sign size, text style,
        /// heights, colours and label positions; adds a missing «НОМЕР» attribute.
        /// </summary>
        internal static void Apply(Transaction transaction, BlockReference reference, PointData data, PointAppearance appearance,
            int decimals, bool resetPositions)
        {
            var database = reference.Database;
            if (!reference.IsWriteEnabled) reference.UpgradeOpen();
            double sign = appearance.EffectiveSignSize;
            double height = appearance.TextHeight ?? 1;
            double numberHeight = appearance.EffectiveNumberHeight;
            reference.ScaleFactors = new Scale3d(sign);
            reference.Rotation = 0;
            // Excluded from the triangulation (задание п. 3): the whole point is grey.
            var excludedColor = Color.FromColorIndex(ColorMethod.ByAci, ExcludedColor);
            reference.Color = data.Excluded ? excludedColor : Color.FromColorIndex(ColorMethod.ByLayer, 256);
            ObjectId style = TextStyle(database, transaction, appearance.TextStyle);
            var definition = (BlockTableRecord)transaction.GetObject(reference.BlockTableRecord, OpenMode.ForRead);
            var present = new Dictionary<string, AttributeReference>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId id in reference.AttributeCollection)
            {
                var attribute = (AttributeReference)transaction.GetObject(id, OpenMode.ForWrite);
                present[attribute.Tag] = attribute;
            }
            foreach (ObjectId id in definition)
            {
                if (!(transaction.GetObject(id, OpenMode.ForRead) is AttributeDefinition attributeDefinition) || attributeDefinition.Constant) continue;
                bool isElevation = string.Equals(attributeDefinition.Tag, Tag, StringComparison.OrdinalIgnoreCase);
                bool isNumber = string.Equals(attributeDefinition.Tag, NumberTag, StringComparison.OrdinalIgnoreCase);
                if (!present.TryGetValue(attributeDefinition.Tag, out var attribute))
                {
                    attribute = new AttributeReference();
                    attribute.SetAttributeFromBlock(attributeDefinition, reference.BlockTransform);
                    reference.AttributeCollection.AppendAttribute(attribute);
                    transaction.AddNewlyCreatedDBObject(attribute, true);
                    resetPositions = true;
                    if (isElevation) attribute.TextString = ElevationFormat.Format(data.Elevation, decimals);
                    if (isNumber) attribute.TextString = data.Number ?? string.Empty;
                }
                if (!isElevation && !isNumber) continue;
                attribute.TextStyleId = style;
                if (transaction.GetObject(style, OpenMode.ForRead) is TextStyleTableRecord record && record.XScale > 0)
                    attribute.WidthFactor = record.XScale;
                attribute.Height = isElevation ? height : numberHeight;
                attribute.Color = data.Excluded ? excludedColor : ParseColor(isElevation ? appearance.ElevationColor : appearance.NumberColor);
                attribute.Invisible = data.HideLabel || (isNumber && (!appearance.ShowNumber || string.IsNullOrEmpty(attribute.TextString)));
                if (resetPositions)
                {
                    attribute.HorizontalMode = TextHorizontalMode.TextLeft;
                    attribute.VerticalMode = TextVerticalMode.TextBase;
                    attribute.Rotation = 0;
                    var p = reference.Position;
                    double x = p.X + sign / 2 + 0.25 * height;
                    attribute.Position = isElevation
                        ? new Point3d(x, p.Y - 0.5 * height, 0)
                        : new Point3d(x, p.Y + 0.25 * height + 0.2 * numberHeight, 0);
                }
            }
        }

        internal static ObjectId TextStyle(Database database, Transaction transaction, string name)
        {
            if (!string.IsNullOrEmpty(name))
            {
                var styles = (TextStyleTable)transaction.GetObject(database.TextStyleTableId, OpenMode.ForRead);
                if (styles.Has(name)) return styles[name];
            }
            return database.Textstyle;
        }

        internal static Color ParseColor(string text)
        {
            if (string.IsNullOrEmpty(text) || text == PointAppearance.ByLayer) return Color.FromColorIndex(ColorMethod.ByLayer, 256);
            if (text.StartsWith("ACI:", StringComparison.Ordinal) &&
                short.TryParse(text.Substring(4), NumberStyles.Integer, CultureInfo.InvariantCulture, out short index))
                return Color.FromColorIndex(ColorMethod.ByAci, index);
            if (text.StartsWith("RGB:", StringComparison.Ordinal))
            {
                var parts = text.Substring(4).Split(',');
                if (parts.Length == 3 && byte.TryParse(parts[0], out byte r) && byte.TryParse(parts[1], out byte g) && byte.TryParse(parts[2], out byte b))
                    return Color.FromRgb(r, g, b);
            }
            return Color.FromColorIndex(ColorMethod.ByLayer, 256);
        }

        internal static string FormatColor(Color color)
        {
            if (color == null || color.IsByLayer) return PointAppearance.ByLayer;
            if (color.IsByAci) return "ACI:" + color.ColorIndex.ToString(CultureInfo.InvariantCulture);
            return "RGB:" + color.Red + "," + color.Green + "," + color.Blue;
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
            if (data.HideLabel)
            {
                buffer.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, "nolbl"));
                buffer.Add(new TypedValue((int)DxfCode.ExtendedDataInteger16, (short)1));
            }
            if (!string.IsNullOrEmpty(data.Number))
            {
                buffer.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, "num"));
                buffer.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, data.Number));
            }
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
                        case "nolbl": data.HideLabel = Convert.ToInt32(value) != 0; break;
                        case "num": data.Number = value as string; break;
                        case "srch": data.SourceHandle = Convert.ToString(value); break;
                        case "mkh": data.MarkerHandle = Convert.ToString(value); break;
                    }
                }
                return hasSurface && hasZ ? data : null;
            }
        }

        internal static string ReadAttribute(BlockReference reference, Transaction transaction, string tag)
        {
            foreach (ObjectId id in reference.AttributeCollection)
            {
                var attribute = (AttributeReference)transaction.GetObject(id, OpenMode.ForRead);
                if (string.Equals(attribute.Tag, tag, StringComparison.OrdinalIgnoreCase)) return attribute.TextString;
            }
            return null;
        }

        internal static string ReadLabel(BlockReference reference, Transaction transaction) => ReadAttribute(reference, transaction, Tag);

        /// <summary>All live point blocks; when <paramref name="surfaceId"/> is set, only that surface.</summary>
        internal static List<SurfacePoint> Read(Database database, Transaction transaction, KzmSettings settings, string surfaceId = null) =>
            Read(database, transaction, settings, surfaceId == null ? (Func<string, bool>)null : id => string.Equals(id, surfaceId, StringComparison.Ordinal));

        /// <summary>
        /// Point blocks of the surfaces accepted by <paramref name="include"/> (all when null). The XData is checked
        /// first: attributes of points of other surfaces are not read (the panel reads only the open project).
        /// </summary>
        internal static List<SurfacePoint> Read(Database database, Transaction transaction, KzmSettings settings, Func<string, bool> include)
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
                if (include != null && !include(data.SurfaceId)) continue;
                string number = ReadAttribute(reference, transaction, NumberTag);
                result.Add(new SurfacePoint
                {
                    Id = id,
                    Handle = reference.Handle.ToString(),
                    Position = new XY(reference.Position.X, reference.Position.Y),
                    Data = data,
                    Elevation = PointElevation.Resolve(data.Elevation, ReadLabel(reference, transaction), settings.Decimals),
                    Number = number != null ? number.Trim() : data.Number,
                });
            }
            return result;
        }

        /// <summary>Applies the appearance of the surface to all its points (elevation texts re-formatted).</summary>
        internal static int ApplyAppearance(Database database, Transaction transaction, SurfaceInfo surface, KzmSettings settings, int previousDecimals)
        {
            EnsureDefinition(database, transaction);
            int count = 0;
            foreach (var point in Read(database, transaction, new KzmSettings { Decimals = previousDecimals }, surface.Id))
            {
                var reference = (BlockReference)transaction.GetObject(point.Id, OpenMode.ForWrite);
                if (point.Elevation.IsValid)
                {
                    point.Data.Elevation = point.Elevation.Value.Value;
                    point.Data.Number = point.Number;
                    reference.XData = Encode(point.Data);
                }
                Apply(transaction, reference, point.Data, surface.Appearance, settings.Decimals, true);
                if (point.Elevation.IsValid)
                    foreach (ObjectId id in reference.AttributeCollection)
                    {
                        var attribute = (AttributeReference)transaction.GetObject(id, OpenMode.ForWrite);
                        if (string.Equals(attribute.Tag, Tag, StringComparison.OrdinalIgnoreCase))
                            attribute.TextString = ElevationFormat.Format(point.Elevation.Value.Value, settings.Decimals);
                    }
                count++;
            }
            return count;
        }

        /// <summary>Fingerprint of the points of a surface (positions, elevations), to detect changes.</summary>
        internal static string FingerprintOf(IEnumerable<SurfacePoint> points)
        {
            var fingerprint = new Fingerprint();
            foreach (var point in points.OrderBy(p => p.Handle, StringComparer.Ordinal))
            {
                fingerprint.Add(point.Handle).Add(point.Position.X).Add(point.Position.Y);
                fingerprint.Add(point.Elevation.IsValid ? point.Elevation.Value.Value.ToString("R", CultureInfo.InvariantCulture) : "?");
                fingerprint.Add(point.Data.Excluded ? "x" : "-");
            }
            return fingerprint.ToString();
        }
    }
}
