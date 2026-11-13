using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Kzm.AutoCAD.Drawing;
using Kzm.Core;

namespace Kzm.AutoCAD.Services
{
    /// <summary>
    /// Оформление картограммы (версия 0.6):
    /// - сетка — один блок, зелёная тонкая линия, только внутри границы работ;
    /// - отметки — блок «КЗМ_УЗЕЛ» с атрибутами «РАБОЧАЯ», «ПРОЕКТНАЯ», «ФАКТИЧЕСКАЯ» в узлах внутри
    ///   границы и в точках пересечения сетки с границей; значения правятся в «Быстрых свойствах»;
    /// - насыпь — красным (объём и рабочая отметка), выемка — синим, штриховка выемки синяя (45°);
    /// - подписи стоят по углам своей точки (как в шаблоне); где тесно — раздвигает пользователь (замечание 16);
    /// - таблица объёмов по столбцам (рис. 8 СТБ 2073-2010) — один блок ниже сетки, с зазором;
    /// - за границей работ ничего не рисуется.
    /// Every created object is listed in <see cref="Created"/>.
    /// </summary>
    internal sealed class CartogramPainter
    {
        internal const string NodeBlock = "КЗМ_УЗЕЛ";
        private static readonly Color Red = Color.FromColorIndex(ColorMethod.ByAci, 1);
        private static readonly Color Blue = Color.FromColorIndex(ColorMethod.ByAci, 5);
        private static readonly Color Green = Color.FromColorIndex(ColorMethod.ByAci, 3);
        private static readonly Color ByLayer = Color.FromColorIndex(ColorMethod.ByLayer, 256);

        private readonly Database database;
        private readonly Transaction transaction;
        private readonly CartogramComputation computation;
        private readonly CartogramInfo c;
        private readonly GridDefinition grid;
        private readonly CartogramResult result;
        private readonly BlockTableRecord space;
        private readonly ObjectId style;
        private readonly double k;
        private readonly MovedLabels moved;
        private ObjectId nodeDefinition;

        internal CartogramPainter(Database database, Transaction transaction, CartogramComputation computation, MovedLabels moved)
        {
            this.database = database;
            this.transaction = transaction;
            this.computation = computation;
            this.moved = moved ?? new MovedLabels();
            c = computation.Info;
            grid = computation.Grid;
            result = computation.Result;
            space = (BlockTableRecord)transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForWrite);
            style = database.Textstyle;
            k = c.UnitsPerMm;
        }

        internal List<ObjectId> Created { get; } = new List<ObjectId>();
        internal int Moved { get; private set; }

        private static Point3d P(XY p) => new Point3d(p.X, p.Y, 0);

        private XY W(double u, double v) => grid.ToWorld(u, v);

        private ResultBuffer Tag(params TypedValue[] extra)
        {
            var buffer = CartogramService.Tag(c.Id);
            foreach (var value in extra) buffer.Add(value);
            return buffer;
        }

        private static TypedValue Text(string value) => new TypedValue((int)DxfCode.ExtendedDataAsciiString, value);

        private T Add<T>(T entity, string layer, ResultBuffer xdata = null) where T : Entity
        {
            entity.Layer = layer;
            entity.XData = xdata ?? Tag();
            Created.Add(space.AppendEntity(entity));
            transaction.AddNewlyCreatedDBObject(entity, true);
            return entity;
        }

        private void Mark(XY at, string label, double height) =>
            Created.AddRange(CheckMarks.Add(transaction, space, at, label, c.Id, height));

        /// <param name="volumes">false — шаг 1: только сетка и отметки; true — всё, с объёмами (замечание 17).</param>
        internal void Draw(bool volumes)
        {
            Layers.Ensure(database, transaction, Layers.Grid, 3);
            foreach (var layer in new[] { Layers.Volumes, Layers.Totals, Layers.Nodes }
                         .Concat(c.Labels().Select(l => l.Style.Layer)).Distinct(StringComparer.OrdinalIgnoreCase))
                Layers.Ensure(database, transaction, layer);
            Layers.Ensure(database, transaction, Layers.CutHatch, 5);
            Layers.EnsureDashed(database, transaction, Layers.ZeroLine);
            Layers.Ensure(database, transaction, Layers.Check, 1, false);
            PointBlocks.EnsureRegApp(database, transaction, CartogramService.App);
            PointBlocks.EnsureRegApp(database, transaction, CheckMarks.AppName);
            if (result.Squares.Count == 0) throw new KzmException("Граница не пересекает ни одного квадрата сетки.");
            nodeDefinition = EnsureNodeDefinition();
            using (var meter = Progress.Start("КЗМ: картограмма «" + c.Name + "»", result.Squares.Count * 4))
            {
                DrawGrid();
                if (volumes)
                {
                    DrawHatches(meter);
                    DrawZeroLines();
                    DrawVolumes(meter);
                }
                DrawLabels(meter);
                if (volumes) DrawTable();
            }
        }

        // ---------- grid ----------

        /// <summary>Anonymous block with all grid lines (clipped by the boundary); green, thin; EXPLODE gives lines.</summary>
        private void DrawGrid()
        {
            var definition = NewBlock("Сетка картограммы «" + c.Name + "» (модуль КЗМ)");
            var weight = (LineWeight)c.GridLineWeight;
            foreach (var (a, b) in result.GridSegments)
                AddTo(definition, new Line(P(a), P(b)) { Layer = Layers.Grid, Color = Green, LineWeight = weight });
            var reference = Add(new BlockReference(Point3d.Origin, definition.ObjectId) { Color = Green, LineWeight = weight }, Layers.Grid,
                Tag(Text(CartogramService.GridMark)));
            MoveToBottom(reference.ObjectId);
        }

        private BlockTableRecord NewBlock(string comment)
        {
            var blocks = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForWrite);
            var definition = new BlockTableRecord { Name = "*U", Origin = Point3d.Origin, Comments = comment };
            blocks.Add(definition);
            transaction.AddNewlyCreatedDBObject(definition, true);
            return definition;
        }

        private void AddTo(BlockTableRecord owner, Entity entity)
        {
            owner.AppendEntity(entity);
            transaction.AddNewlyCreatedDBObject(entity, true);
        }

        private void MoveToBottom(ObjectId id)
        {
            try
            {
                var order = (DrawOrderTable)transaction.GetObject(space.DrawOrderTableId, OpenMode.ForWrite);
                order.MoveToBottom(new ObjectIdCollection(new[] { id }));
            }
            catch (Autodesk.AutoCAD.Runtime.Exception) { /* draw order is cosmetic */ }
        }

        // ---------- cut hatch, zero lines ----------

        /// <summary>
        /// Площадь выемок — штриховкой под углом 45° (СТБ 2073-2010, п. 7.1 б), синим, тонкой линией. Шаг задан
        /// в мм на листе: штриховка своими линиями («_USER») не зависит от образцов acad.pat / acadiso.pat и
        /// от системы единиц чертежа (замечание 15: в дюймовом чертеже ANSI31 сливалась в сплошную заливку).
        /// </summary>
        private void DrawHatches(Progress meter)
        {
            double spacing = c.HatchSpacingMm * k;
            var weight = (LineWeight)c.HatchLineWeight;
            foreach (var square in result.Squares.Where(s => s.Computed && s.CutPolygons.Count > 0))
            {
                meter.Step();
                var hatch = Add(new Hatch(), Layers.CutHatch);
                try
                {
                    // Angle and spacing go before SetHatchPattern, otherwise they do not take effect.
                    hatch.PatternAngle = Math.PI / 4;
                    hatch.PatternSpace = spacing;
                    hatch.PatternDouble = false;
                    hatch.SetHatchPattern(HatchPatternType.UserDefined, "_USER");
                    hatch.PatternAngle = Math.PI / 4;
                    hatch.PatternSpace = spacing;
                    hatch.Associative = false;
                    hatch.Color = Blue;
                    hatch.LineWeight = weight;
                    foreach (var polygon in square.CutPolygons)
                    {
                        var points = new Point2dCollection();
                        var bulges = new DoubleCollection();
                        foreach (var p in polygon) { points.Add(new Point2d(p.X, p.Y)); bulges.Add(0); }
                        points.Add(new Point2d(polygon[0].X, polygon[0].Y));
                        bulges.Add(0);
                        hatch.AppendLoop(HatchLoopTypes.Default, points, bulges);
                    }
                    hatch.EvaluateHatch(true);
                }
                catch (Autodesk.AutoCAD.Runtime.Exception)
                {
                    Created.Remove(hatch.ObjectId);
                    hatch.Erase();
                    computation.Problems.Add("штриховка выемки в квадрате у " + CartogramExport.Center(result, square.I, square.J) + " не построена");
                }
            }
        }

        /// <summary>Линия нулевых работ — штриховая тонкая (СТБ 2073-2010, п. 3.12).</summary>
        private void DrawZeroLines()
        {
            foreach (var line in result.ZeroLines.Where(l => l.Count >= 2))
            {
                var polyline = new Polyline();
                for (int i = 0; i < line.Count; i++) polyline.AddVertexAt(i, new Point2d(line[i].X, line[i].Y), 0, 0, 0);
                polyline.LinetypeScale = c.Scale / 2000.0;
                Add(polyline, Layers.ZeroLine);
            }
        }

        // ---------- volumes ----------

        private DBText Label(string value, XY at, double height, TextHorizontalMode horizontal, TextVerticalMode vertical, string layer, Color color, string key)
        {
            var point = P(at);
            var text = new DBText
            {
                TextString = value, Height = height, TextStyleId = style, Rotation = 0,
                HorizontalMode = horizontal, VerticalMode = vertical, Position = point,
            };
            if (horizontal != TextHorizontalMode.TextLeft || vertical != TextVerticalMode.TextBase) text.AlignmentPoint = point;
            Add(text, layer, Tag(Text(CartogramService.LabelMark), Text(key), new TypedValue((int)DxfCode.ExtendedDataXCoordinate, point)));
            if (color != null) text.Color = color;
            text.AdjustAlignment(database);
            if (moved.Texts.TryGetValue(key, out var offset))
            {
                text.TransformBy(Matrix3d.Displacement(offset));
                Moved++;
            }
            return text;
        }

        /// <summary>Объёмы в квадратах: насыпь «+…» красным, выемка «-…» синим.</summary>
        private void DrawVolumes(Progress meter)
        {
            double h = c.VolumeTextMm * k;
            foreach (var square in result.Squares)
            {
                meter.Step();
                if (!square.Computed)
                {
                    Mark(square.LabelPoint, "не рассчитан", 0.6 * h);
                    continue;
                }
                string fill = CartogramService.Volume(square.Fill, c.VolumeDecimals, true);
                string cut = CartogramService.Volume(square.Cut, c.VolumeDecimals, false);
                bool hasFill = fill != "0", hasCut = cut != "0";
                string key = ":" + square.I.ToString(CultureInfo.InvariantCulture) + ":" + square.J.ToString(CultureInfo.InvariantCulture);
                var at = square.LabelPoint;
                if (hasFill && hasCut)
                {
                    Label(fill, new XY(at.X, at.Y + 0.15 * h), h, TextHorizontalMode.TextCenter, TextVerticalMode.TextBottom, Layers.Volumes, Red, "V+" + key);
                    Label(cut, new XY(at.X, at.Y - 0.15 * h), h, TextHorizontalMode.TextCenter, TextVerticalMode.TextTop, Layers.Volumes, Blue, "V-" + key);
                }
                else
                {
                    Label(hasCut ? cut : fill, at, h, TextHorizontalMode.TextCenter, TextVerticalMode.TextVerticalMid, Layers.Volumes,
                        hasCut ? Blue : hasFill ? Red : null, "V" + key);
                }
            }
        }

        // ---------- node labels ----------

        /// <summary>Block «КЗМ_УЗЕЛ»: no geometry, three attributes (their places and colours are set in each insert).</summary>
        private ObjectId EnsureNodeDefinition()
        {
            var blocks = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            BlockTableRecord definition;
            if (blocks.Has(NodeBlock)) definition = (BlockTableRecord)transaction.GetObject(blocks[NodeBlock], OpenMode.ForWrite);
            else
            {
                blocks.UpgradeOpen();
                definition = new BlockTableRecord { Name = NodeBlock, Origin = Point3d.Origin, Comments = "Отметки узла картограммы (модуль КЗМ)." };
                blocks.Add(definition);
                transaction.AddNewlyCreatedDBObject(definition, true);
            }
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId id in definition)
                if (transaction.GetObject(id, OpenMode.ForRead) is AttributeDefinition attribute) present.Add(attribute.Tag);
            foreach (var (tag, prompt) in new[]
                     {
                         (CartogramService.WorkingTag, "Рабочая отметка (Hпр − Hф)"),
                         (CartogramService.ProjectTag, "Проектная отметка"),
                         (CartogramService.FactTag, "Фактическая отметка"),
                     })
            {
                if (present.Contains(tag)) continue;
                var attribute = new AttributeDefinition(Point3d.Origin, string.Empty, tag, prompt, database.Textstyle) { Height = 1, Layer = "0" };
                definition.AppendEntity(attribute);
                transaction.AddNewlyCreatedDBObject(attribute, true);
            }
            return definition.ObjectId;
        }

        private sealed class NodeLabel
        {
            internal BlockReference Block;
            internal string Key;
            internal XY Anchor;
        }

        /// <summary>
        /// Подписи в узлах внутри границы и в точках пересечения сетки с границей — по углам точки, как в
        /// шаблоне (пример Б.4). Где тесно, подписи раздвигает пользователь; при пересчёте сдвинутые им
        /// подписи ставятся туда же (задание п. 7).
        /// </summary>
        private void DrawLabels(Progress meter)
        {
            double h = c.NodeTextMm * k;
            var labels = new List<NodeLabel>();
            foreach (var node in result.Nodes.Values.Where(n => n.Used && n.Inside).OrderByDescending(n => n.J).ThenBy(n => n.I))
            {
                meter.Step();
                if (!node.Working.HasValue)
                {
                    Mark(node.Position, "нет данных", 0.6 * h);
                    continue;
                }
                labels.Add(NodeLabelAt("N:" + node.I.ToString(CultureInfo.InvariantCulture) + ":" + node.J.ToString(CultureInfo.InvariantCulture),
                    node.Position, node.Fact.Value, node.Project.Value, h));
            }
            foreach (var point in result.BoundaryPoints)
            {
                if (!point.Working.HasValue)
                {
                    Mark(point.Position, "нет данных", 0.6 * h);
                    continue;
                }
                labels.Add(NodeLabelAt(point.Key, point.Position, point.Fact.Value, point.Project.Value, h));
            }
            foreach (var label in labels) Stamp(label);
        }

        private NodeLabel NodeLabelAt(string key, XY at, double fact, double project, double h)
        {
            double working = project - fact;
            var reference = Add(new BlockReference(P(at), nodeDefinition), Layers.Nodes);
            var definition = (BlockTableRecord)transaction.GetObject(nodeDefinition, OpenMode.ForRead);
            var label = new NodeLabel { Block = reference, Key = key, Anchor = at };
            moved.Nodes.TryGetValue(key, out var offsets);
            bool shifted = false;
            foreach (ObjectId id in definition)
            {
                if (!(transaction.GetObject(id, OpenMode.ForRead) is AttributeDefinition attributeDefinition) || attributeDefinition.Constant) continue;
                var attribute = new AttributeReference();
                attribute.SetAttributeFromBlock(attributeDefinition, reference.BlockTransform);
                reference.AttributeCollection.AppendAttribute(attribute);
                transaction.AddNewlyCreatedDBObject(attribute, true);
                int index;
                NodeLabelStyle labelStyle;
                string value;
                Color color;
                switch (attributeDefinition.Tag.ToUpperInvariant())
                {
                    case CartogramService.WorkingTag:
                        index = 0; labelStyle = c.WorkingLabel; value = ElevationFormat.FormatSigned(working, 2);
                        // Насыпь — красным, выемка — синим (по знаку рабочей отметки, как она подписана).
                        color = value.StartsWith("+", StringComparison.Ordinal) ? Red : value.StartsWith("-", StringComparison.Ordinal) ? Blue : ByLayer;
                        break;
                    case CartogramService.ProjectTag:
                        index = 1; labelStyle = c.ProjectLabel; value = ElevationFormat.Format(project, 2); color = PointBlocks.ParseColor(labelStyle.Color);
                        break;
                    case CartogramService.FactTag:
                        index = 2; labelStyle = c.FactLabel; value = ElevationFormat.Format(fact, 2); color = PointBlocks.ParseColor(labelStyle.Color);
                        break;
                    default:
                        continue;
                }
                PlaceAttribute(attribute, labelStyle, at, value, color, h);
                if (offsets != null && offsets[index].Length > 1e-6)
                {
                    attribute.TransformBy(Matrix3d.Displacement(offsets[index]));
                    shifted = true;
                }
            }
            if (shifted) Moved++;
            reference.XData = Tag(Text(CartogramService.NodeMark), Text(key),
                new TypedValue((int)DxfCode.ExtendedDataReal, fact),
                new TypedValue((int)DxfCode.ExtendedDataReal, project),
                new TypedValue((int)DxfCode.ExtendedDataReal, working),
                new TypedValue((int)DxfCode.ExtendedDataXCoordinate, P(at)));
            return label;
        }

        private void PlaceAttribute(AttributeReference attribute, NodeLabelStyle labelStyle, XY at, string value, Color color, double h)
        {
            bool left = labelStyle.Corner == LabelCorner.TopLeft || labelStyle.Corner == LabelCorner.BottomLeft;
            bool top = labelStyle.Corner == LabelCorner.TopLeft || labelStyle.Corner == LabelCorner.TopRight;
            double g = 0.35 * h;
            var point = new Point3d(at.X + (left ? -g : g), at.Y + (top ? g : -g), 0);
            attribute.TextString = value;
            attribute.Height = h;
            attribute.TextStyleId = style;
            attribute.Rotation = 0;
            attribute.WidthFactor = 1;
            attribute.HorizontalMode = left ? TextHorizontalMode.TextRight : TextHorizontalMode.TextLeft;
            attribute.VerticalMode = top ? TextVerticalMode.TextBottom : TextVerticalMode.TextTop;
            attribute.Position = point;
            attribute.AlignmentPoint = point;
            attribute.Layer = labelStyle.Layer;
            attribute.Color = color ?? ByLayer;
            attribute.Invisible = false;
            attribute.AdjustAlignment(database);
        }

        /// <summary>
        /// Writes into the node block where the module finally put each attribute: a later move by the user
        /// is measured from here (задание п. 7), a typed value is compared with the written ones.
        /// </summary>
        private void Stamp(NodeLabel label)
        {
            List<TypedValue> values;
            using (var xdata = label.Block.GetXDataForApplication(CartogramService.App)) values = xdata.AsArray().ToList();
            var attributes = Sources.Attributes(label.Block, transaction);
            var tags = new[] { CartogramService.WorkingTag, CartogramService.ProjectTag, CartogramService.FactTag };
            foreach (var tag in tags)
            {
                var anchor = attributes.TryGetValue(tag, out var attribute) ? Sources.TextAnchor(attribute) : label.Block.Position;
                values.Add(new TypedValue((int)DxfCode.ExtendedDataXCoordinate, anchor));
            }
            // The texts as written: a later change shows which value the user typed (NodeSync).
            foreach (var tag in tags)
                values.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, attributes.TryGetValue(tag, out var attribute) ? attribute.TextString ?? string.Empty : string.Empty));
            label.Block.XData = new ResultBuffer(values.ToArray());
        }

        // ---------- table under the grid (рис. 8 СТБ 2073-2010) ----------

        /// <summary>
        /// Таблица под сеткой — один блок ниже сетки с зазором: насыпь и выемка по столбцам (столбцы таблицы —
        /// под столбцами сетки), «Итого», «Всего». Насыпь — красным, выемка — синим.
        /// </summary>
        private void DrawTable()
        {
            var definition = NewBlock("Таблица объёмов картограммы «" + c.Name + "» (модуль КЗМ)");
            double s = grid.Step, rh = 8 * k, th = c.TotalsTextMm * k;
            double u0 = result.IMin * s, u1 = (result.IMax + 1) * s, top = result.JMin * s - c.TableGapMm * k;
            double left = u0 - 25 * k, labelLeft = u0 - 20 * k, right = u1 + 25 * k, totalLeft = u1 + 5 * k;
            void Line(double x1, double y1, double x2, double y2) =>
                AddTo(definition, new Line(P(W(x1, y1)), P(W(x2, y2))) { Layer = Layers.Totals });
            void Write(string value, double u, double v, Color color, double rotation = 0, TextHorizontalMode horizontal = TextHorizontalMode.TextCenter)
            {
                if (string.IsNullOrEmpty(value)) return;
                var point = P(W(u, v));
                var text = new DBText
                {
                    TextString = value, Height = th, TextStyleId = style, Rotation = rotation, HorizontalMode = horizontal,
                    VerticalMode = TextVerticalMode.TextVerticalMid, Position = point, AlignmentPoint = point, Layer = Layers.Totals,
                };
                if (color != null) text.Color = color;
                AddTo(definition, text);
                text.AdjustAlignment(database);
            }
            Line(left, top, right, top);
            Line(labelLeft, top - rh, u1, top - rh);
            Line(totalLeft, top - rh, right, top - rh);
            Line(left, top - 2 * rh, right, top - 2 * rh);
            foreach (double u in new[] { left, labelLeft, totalLeft, right }) Line(u, top, u, top - 2 * rh);
            for (int i = result.IMin; i <= result.IMax + 1; i++) Line(i * s, top, i * s, top - 2 * rh);
            Write("Итого, м³", (left + labelLeft) / 2, top - rh, null, Math.PI / 2);
            Write("Насыпь (+)", labelLeft + 1.5 * k, top - rh / 2, Red, 0, TextHorizontalMode.TextLeft);
            Write("Выемка (–)", labelLeft + 1.5 * k, top - 1.5 * rh, Blue, 0, TextHorizontalMode.TextLeft);
            for (int i = result.IMin; i <= result.IMax; i++)
            {
                result.ColumnTotals.TryGetValue(i, out var total);
                Write(CartogramService.Volume(total.Fill, c.VolumeDecimals, true), (i + 0.5) * s, top - rh / 2, Red);
                Write(CartogramService.Volume(total.Cut, c.VolumeDecimals, false), (i + 0.5) * s, top - 1.5 * rh, Blue);
            }
            Write("Всего, м³", (u1 + totalLeft) / 2, top - rh, null, Math.PI / 2);
            Write(CartogramService.Volume(result.TotalFill, c.VolumeDecimals, true), (totalLeft + right) / 2, top - rh / 2, Red);
            Write(CartogramService.Volume(result.TotalCut, c.VolumeDecimals, false), (totalLeft + right) / 2, top - 1.5 * rh, Blue);
            var reference = Add(new BlockReference(Point3d.Origin, definition.ObjectId), Layers.Totals, Tag(Text(CartogramService.TableMark)));
            if (moved.Table.HasValue)
            {
                reference.TransformBy(Matrix3d.Displacement(moved.Table.Value));
                Moved++;
            }
        }
    }
}
