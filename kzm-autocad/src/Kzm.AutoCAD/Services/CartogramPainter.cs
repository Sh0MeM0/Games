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
    /// Оформление картограммы (версии 0.6–0.8):
    /// - сетка — один блок, зелёная тонкая линия, только внутри границы работ;
    /// - отметки — блок «КЗМ_УЗЕЛ» с атрибутами «РАБОЧАЯ», «ПРОЕКТНАЯ», «ФАКТИЧЕСКАЯ» в узлах внутри
    ///   границы и в точках пересечения сетки с границей; значения правятся в «Быстрых свойствах»;
    /// - насыпь — красным (объём и рабочая отметка), выемка — синим, штриховка выемки ANSI31 синяя (45°);
    /// - подписи стоят по углам своей точки (как в шаблоне); где тесно — раздвигает пользователь (замечание 16);
    /// - таблица объёмов по столбцам (рис. 8 СТБ 2073-2010) — один блок ниже сетки, с зазором; под ней — площади;
    /// - толщина линий (замечания 21, 22): отметки, объёмы, таблицы — 0,25 мм; линия нулевых работ и граница — 0,30 мм;
    /// - за границей работ ничего не рисуется.
    /// Every created object is listed in <see cref="Created"/>.
    /// </summary>
    internal sealed class CartogramPainter
    {
        internal const string NodeBlock = "КЗМ_УЗЕЛ";
        /// <summary>Отметки, объёмы, таблицы и площади (замечание 21).</summary>
        internal const LineWeight TextWeight = LineWeight.LineWeight025;
        /// <summary>Граница картограммы и линия нулевых работ (замечание 22).</summary>
        internal const LineWeight OutlineWeight = LineWeight.LineWeight030;
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
            style = computation.TextStyle.IsNull ? database.Textstyle : computation.TextStyle;
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
                if (volumes)
                {
                    double bottom = DrawTable();
                    DrawAreas(bottom);
                }
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
        /// Площадь выемок — штриховкой под 45° (СТБ 2073-2010, п. 7.1 б), синей, тонкой линией, с шагом в мм на листе.
        /// С версии 0.9.1 линии строит сам модуль (<see cref="HatchLines"/>) — один блок на картограмму, — а не образец
        /// ANSI31: в AutoCAD мелкие штриховки ANSI31 выходили сплошными (замечание 33), а шаг своих линий точен всегда.
        /// Мелкая выемка не штрихуется (замечания 26, 33).
        /// </summary>
        private void DrawHatches(Progress meter)
        {
            double spacing = c.HatchSpacingMm * k;
            var weight = (LineWeight)c.HatchLineWeight;
            var pieces = result.Squares.Where(s => s.Computed).SelectMany(s => s.CutPolygons).ToList();
            meter.Step();
            // Pieces that touch form a region; a region smaller than a square with the side of two hatch steps gets no hatch.
            double minimum = HatchRegions.MinimumArea(spacing);
            var kept = HatchRegions.Kept(pieces.Select(piece => (IReadOnlyList<XY>)piece).ToList(), minimum);
            if (kept.Count < pieces.Count)
            {
                double area = pieces.Where((piece, index) => !kept.Contains(index)).Sum(piece => Geometry.Area(piece));
                computation.Notes.Add("Мелкая выемка без штриховки: кусков " + (pieces.Count - kept.Count) + ", всего " + ElevationFormat.Format(area, 1) +
                                      " м² (участки меньше " + ElevationFormat.Format(minimum, 1) + " м² — квадрата со стороной в два шага штриховки).");
            }
            var segments = HatchLines.Build(pieces.Where((piece, index) => kept.Contains(index)).Select(piece => (IReadOnlyList<XY>)piece), spacing, grid.Origin);
            if (segments.Count == 0) return;
            var definition = NewBlock("Штриховка выемки картограммы «" + c.Name + "» (модуль КЗМ)");
            foreach (var (a, b) in segments)
                AddTo(definition, new Line(P(a), P(b)) { Layer = Layers.CutHatch, Color = Blue, LineWeight = weight });
            var reference = Add(new BlockReference(Point3d.Origin, definition.ObjectId) { Color = Blue, LineWeight = weight }, Layers.CutHatch,
                Tag(Text(CartogramService.HatchMark)));
            MoveToBottom(reference.ObjectId);
        }

        /// <summary>Линия нулевых работ — штриховая (СТБ 2073-2010, п. 3.12), толщиной 0,30 мм (замечание 22).</summary>
        private void DrawZeroLines()
        {
            foreach (var line in result.ZeroLines.Where(l => l.Count >= 2))
            {
                var polyline = new Polyline();
                for (int i = 0; i < line.Count; i++) polyline.AddVertexAt(i, new Point2d(line[i].X, line[i].Y), 0, 0, 0);
                polyline.LinetypeScale = c.Scale / 2000.0;
                Add(polyline, Layers.ZeroLine);
                polyline.LineWeight = OutlineWeight;
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
            text.LineWeight = TextWeight;
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
            {
                if (!(transaction.GetObject(id, OpenMode.ForRead) is AttributeDefinition attribute)) continue;
                present.Add(attribute.Tag);
                // One grip per node (замечание 32): the values move only together with the block.
                if (!attribute.LockPositionInBlock)
                {
                    attribute.UpgradeOpen();
                    attribute.LockPositionInBlock = true;
                }
            }
            foreach (var (tag, prompt) in new[]
                     {
                         (CartogramService.WorkingTag, "Рабочая отметка (Hпр − Hф)"),
                         (CartogramService.ProjectTag, "Проектная отметка"),
                         (CartogramService.FactTag, "Фактическая отметка"),
                     })
            {
                if (present.Contains(tag)) continue;
                var attribute = new AttributeDefinition(Point3d.Origin, string.Empty, tag, prompt, database.Textstyle) { Height = 1, Layer = "0", LockPositionInBlock = true };
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
            /// <summary>The labels were dragged away from the node: a leader with a shelf is drawn (замечание 25).</summary>
            internal bool Shifted;
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
            foreach (var label in labels)
            {
                Stamp(label);
                if (label.Shifted) DrawLeader(label);
            }
        }

        private NodeLabel NodeLabelAt(string key, XY at, double fact, double project, double h)
        {
            double working = WorkingMark.Of(project, fact);
            // Labels the user dragged away by the grip of the block stay there, with a leader (замечание 25).
            bool dragged = moved.NodeShifts.TryGetValue(key, out var drag);
            var place = dragged ? new XY(at.X + drag.X, at.Y + drag.Y) : at;
            var reference = Add(new BlockReference(P(place), nodeDefinition), Layers.Nodes);
            reference.LineWeight = TextWeight;
            var definition = (BlockTableRecord)transaction.GetObject(nodeDefinition, OpenMode.ForRead);
            var label = new NodeLabel { Block = reference, Key = key, Anchor = at, Shifted = dragged };
            moved.Nodes.TryGetValue(key, out var offsets);
            bool shifted = false;
            foreach (ObjectId id in definition)
            {
                if (!(transaction.GetObject(id, OpenMode.ForRead) is AttributeDefinition attributeDefinition) || attributeDefinition.Constant) continue;
                var attribute = new AttributeReference();
                attribute.SetAttributeFromBlock(attributeDefinition, reference.BlockTransform);
                // No grips on the values themselves: only the grip of the block at the corner of the grid (замечание 32).
                attribute.LockPositionInBlock = true;
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
                PlaceAttribute(attribute, labelStyle, place, value, color, h);
                if (offsets != null && offsets[index].Length > 1e-6)
                {
                    attribute.TransformBy(Matrix3d.Displacement(offsets[index]));
                    shifted = true;
                }
            }
            if (shifted) Moved++;
            if (label.Shifted) Moved++;
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
            attribute.LineWeight = TextWeight;
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
            // [14] where the block was put (a later drag of the whole block is told from moves of single labels),
            // [15] the handle of its leader (empty — none).
            values.Add(new TypedValue((int)DxfCode.ExtendedDataXCoordinate, label.Block.Position));
            values.Add(new TypedValue((int)DxfCode.ExtendedDataAsciiString, string.Empty));
            label.Block.XData = new ResultBuffer(values.ToArray());
        }

        /// <summary>Leader of dragged labels: node → corner of the labels → shelf under the right-hand labels (замечание 25).</summary>
        private void DrawLeader(NodeLabel label)
        {
            var leader = NodeLeader.Create(transaction, label.Block, P(label.Anchor), c.Id, label.Key, space);
            Created.Add(leader.ObjectId);
            NodeLeader.Remember(label.Block, leader);
        }

        // ---------- table under the grid (рис. 8 СТБ 2073-2010) ----------

        /// <summary>
        /// Таблица под сеткой по рис. 8 СТБ 2073-2010 (замечание 20) — один блок ниже сетки с зазором. Слева графы 5 мм
        /// («Итого, м³» на обе строки) и 20 мм («Насыпь (+)», «Выемка (–)»), затем графы шириной l под столбцами сетки,
        /// справа графы 5 мм («Всего, м³» в верхней клетке, нижняя пустая — как на рисунке) и 20 мм (объёмы по всей
        /// территории). Строки по 8 мм. Насыпь — красным, выемка — синим; линии и текст — 0,25 мм (замечание 21).
        /// Возвращает низ таблицы (по оси Y сетки, без сдвига пользователем).
        /// </summary>
        private double DrawTable()
        {
            var definition = NewBlock("Таблица объёмов картограммы «" + c.Name + "» (модуль КЗМ)");
            double s = grid.Step, rh = 8 * k, th = c.TotalsTextMm * k, margin = 1 * k;
            double u0 = result.IMin * s, u1 = (result.IMax + 1) * s, top = result.JMin * s - c.TableGapMm * k, bottom = top - 2 * rh;
            double left = u0 - 25 * k, labelLeft = u0 - 20 * k, totalLeft = u1 + 5 * k, right = u1 + 25 * k;
            void Line(double x1, double y1, double x2, double y2) =>
                AddTo(definition, new Line(P(W(x1, y1)), P(W(x2, y2))) { Layer = Layers.Totals, LineWeight = TextWeight });
            // A text centred at (u, v), squeezed into «room» along its direction when it is longer.
            void Write(string value, double u, double v, double room, Color color, double height, bool vertical = false)
            {
                if (string.IsNullOrEmpty(value)) return;
                var point = P(W(u, v));
                var text = new DBText
                {
                    TextString = value, Height = height, TextStyleId = style, Rotation = vertical ? Math.PI / 2 : 0,
                    HorizontalMode = TextHorizontalMode.TextCenter, VerticalMode = TextVerticalMode.TextVerticalMid,
                    Position = point, AlignmentPoint = point, Layer = Layers.Totals, LineWeight = TextWeight,
                };
                if (color != null) text.Color = color;
                AddTo(definition, text);
                text.AdjustAlignment(database);
                Fit(text, room, vertical);
            }
            Line(left, top, right, top);
            // The middle line: not across «Итого, м³» (one cell for both rows), but across the right 5 mm column (рис. 8).
            Line(labelLeft, top - rh, right, top - rh);
            Line(left, bottom, right, bottom);
            foreach (double u in new[] { left, labelLeft, totalLeft, right }) Line(u, top, u, bottom);
            for (int i = result.IMin; i <= result.IMax + 1; i++) Line(i * s, top, i * s, bottom);
            Write("Итого, м³", (left + labelLeft) / 2, top - rh, 2 * rh - 2 * margin, null, Math.Min(th, 2.5 * k), true);
            Write("Насыпь (+)", (labelLeft + u0) / 2, top - rh / 2, 20 * k - 2 * margin, Red, th);
            Write("Выемка (–)", (labelLeft + u0) / 2, top - 1.5 * rh, 20 * k - 2 * margin, Blue, th);
            for (int i = result.IMin; i <= result.IMax; i++)
            {
                // A column without squares (between parts of the boundary, замечание 31) stays empty.
                if (!result.ColumnTotals.TryGetValue(i, out var total)) continue;
                Write(CartogramService.Volume(total.Fill, c.VolumeDecimals, true), (i + 0.5) * s, top - rh / 2, s - 2 * margin, Red, th);
                Write(CartogramService.Volume(total.Cut, c.VolumeDecimals, false), (i + 0.5) * s, top - 1.5 * rh, s - 2 * margin, Blue, th);
            }
            // «Всего, м³» — two short lines across the 5 mm column (as in рис. 8): «Всего,» on the left, «м³» on the right.
            double hv = Math.Min(th, 1.8 * k), pitch = 1.25 * hv, center = (u1 + totalLeft) / 2;
            Write("Всего,", center - pitch / 2, top - rh / 2, rh - margin, null, hv, true);
            Write("м³", center + pitch / 2, top - rh / 2, rh - margin, null, hv, true);
            Write(CartogramService.Volume(result.TotalFill, c.VolumeDecimals, true), (totalLeft + right) / 2, top - rh / 2, 20 * k - 2 * margin, Red, th);
            Write(CartogramService.Volume(result.TotalCut, c.VolumeDecimals, false), (totalLeft + right) / 2, top - 1.5 * rh, 20 * k - 2 * margin, Blue, th);
            var reference = Add(new BlockReference(Point3d.Origin, definition.ObjectId) { LineWeight = TextWeight }, Layers.Totals,
                Tag(Text(CartogramService.TableMark)));
            if (moved.Table.HasValue)
            {
                reference.TransformBy(Matrix3d.Displacement(moved.Table.Value));
                Moved++;
            }
            return bottom;
        }

        /// <summary>
        /// Squeezes a text into <paramref name="room"/> (drawing units along the text) when it is longer: first narrower
        /// letters (width factor down to 0.7), then a smaller height. A text that fits is left as it is.
        /// </summary>
        private void Fit(DBText text, double room, bool vertical)
        {
            if (!(room > 0)) return;
            double length;
            try
            {
                var extents = text.GeometricExtents;
                length = vertical ? extents.MaxPoint.Y - extents.MinPoint.Y : extents.MaxPoint.X - extents.MinPoint.X;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                return; // extents unknown (font not found): keep the text as it is
            }
            if (!(length > room)) return;
            double width = Math.Max(0.7, text.WidthFactor * room / length);
            double squeezed = length * width / text.WidthFactor;
            text.WidthFactor = width;
            if (squeezed > room) text.Height *= room / squeezed;
            text.AdjustAlignment(database);
        }

        // ---------- areas (замечание 23) ----------

        /// <summary>
        /// Площади насыпи, выемки, 0-области и всей картограммы — текст по образцу пользователя, отдельным блоком под
        /// таблицей объёмов (с зазором 5 мм). Перенесённый пользователем блок при пересчёте остаётся на новом месте,
        /// иначе он идёт за таблицей. Площади — по тем же частям квадратов, что и объёмы (см. <see cref="CartogramAreas"/>).
        /// </summary>
        private void DrawAreas(double tableBottom)
        {
            var lines = CartogramAreas.Lines(result);
            var definition = NewBlock("Площади картограммы «" + c.Name + "» (модуль КЗМ)");
            double th = c.TotalsTextMm * k, pitch = th * 5 / 3;
            double left = result.IMin * grid.Step - 25 * k, top = tableBottom - 5 * k;
            for (int i = 0; i < lines.Count; i++)
            {
                AddTo(definition, new DBText
                {
                    TextString = lines[i], Height = th, TextStyleId = style, Rotation = 0,
                    Position = P(W(left, top - th - i * pitch)), Layer = Layers.Totals, LineWeight = TextWeight,
                });
            }
            var shift = moved.Areas ?? moved.Table ?? new Vector3d();
            var reference = Add(new BlockReference(Point3d.Origin, definition.ObjectId) { LineWeight = TextWeight }, Layers.Totals,
                Tag(Text(CartogramService.AreasMark), new TypedValue((int)DxfCode.ExtendedDataXCoordinate, Point3d.Origin + shift)));
            if (shift.Length > 1e-9) reference.TransformBy(Matrix3d.Displacement(shift));
            if (moved.Areas.HasValue) Moved++;
        }
    }
}
