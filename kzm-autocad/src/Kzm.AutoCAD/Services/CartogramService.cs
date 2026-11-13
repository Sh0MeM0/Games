using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Kzm.AutoCAD.Drawing;
using Kzm.Core;

namespace Kzm.AutoCAD.Services
{
    internal sealed class CartogramComputation
    {
        internal CartogramInfo Info { get; set; }
        internal SurfaceModel Fact { get; set; }
        internal SurfaceModel Project { get; set; }
        internal List<XY> Boundary { get; set; }
        internal GridDefinition Grid { get; set; }
        internal CartogramResult Result { get; set; }
        /// <summary>Контрольный расчёт по наложению триангуляций (null — выключен или не выполнен).</summary>
        internal ControlVolumeResult Control { get; set; }
        internal string Fingerprint { get; set; }
        internal List<string> Problems { get; } = new List<string>();
        /// <summary>Places worth a look: squares and nodes without surface data.</summary>
        internal List<(string Text, XY At)> Marks { get; } = new List<(string, XY)>();
        /// <summary>Labels put back where the user had moved them (after a rebuild).</summary>
        internal int MovedLabels { get; set; }
    }

    /// <summary>
    /// Картограмма: расчёт по TIN фактической и проектной поверхностей и оформление на чертеже
    /// по СТБ 2073-2010 (п. 3.12, 7.1–7.3, рис. 8, пример Б.4). Все построенные объекты
    /// помечены XData «SH0MEM0_KZM_CART» с id картограммы; их список хранится в чертеже, поэтому
    /// пересчёт не перебирает весь чертёж. Ведомость объёмов при пересчёте не стирается:
    /// в ней обновляется только строка «Грунт планировки территории».
    /// </summary>
    internal static class CartogramService
    {
        internal const string App = "SH0MEM0_KZM_CART";
        private const string StatementMark = "STATEMENT";
        private const string LabelMark = "L";
        private const string StatementsSuffix = ".statements";
        internal const string PlanningRow = "Грунт планировки территории";

        internal static GridDefinition GridFor(CartogramInfo c, IReadOnlyList<XY> boundary)
        {
            double angle = c.AngleDegrees * Math.PI / 180;
            if (c.OriginX.HasValue && c.OriginY.HasValue) return new GridDefinition(c.Step, new XY(c.OriginX.Value, c.OriginY.Value), angle);
            // Auto: the grid lines are multiples of the step in the rotated world axes.
            var provisional = new GridDefinition(c.Step, new XY(0, 0), angle);
            var local = boundary.Select(provisional.ToLocal).ToList();
            double u = Math.Floor(local.Min(p => p.U) / c.Step) * c.Step;
            double v = Math.Floor(local.Min(p => p.V) / c.Step) * c.Step;
            return new GridDefinition(c.Step, provisional.ToWorld(u, v), angle);
        }

        /// <summary>What the cartogram depends on: points, structure lines and boundaries of both surfaces, its boundary and parameters.</summary>
        internal static string FingerprintOf(Database database, Transaction transaction, CartogramInfo c, IReadOnlyList<SurfaceInfo> surfaces,
            Func<string, SurfaceSignature> signatureOf)
        {
            var fingerprint = new Fingerprint();
            foreach (var id in new[] { c.FactSurfaceId, c.ProjectSurfaceId })
            {
                var surface = surfaces.FirstOrDefault(s => s.Id == id);
                fingerprint.Add(id ?? "-");
                if (surface == null) continue;
                signatureOf(id).AddTo(fingerprint);
            }
            fingerprint.Add(SurfaceModels.OutlineFingerprint(database, transaction, c.BoundaryHandle));
            foreach (var pair in CartogramInfo.FingerprintPairs(c)) fingerprint.Add(pair.Key).Add(pair.Value);
            return fingerprint.ToString();
        }

        /// <summary>Calculation only (the drawing is not changed): used by «Проверить», the export and the build.</summary>
        internal static CartogramComputation Compute(Database database, Transaction transaction, CartogramInfo c, KzmSettings settings)
        {
            var problems = c.Validate();
            if (problems.Count > 0) throw new KzmException("Картограмма «" + c.Name + "»: " + string.Join("; ", problems) + ".");
            var surfaces = KzmStore.ReadSurfaces(database, transaction);
            var fact = surfaces.FirstOrDefault(s => s.Id == c.FactSurfaceId) ?? throw new KzmException("Фактическая поверхность картограммы удалена.");
            var project = surfaces.FirstOrDefault(s => s.Id == c.ProjectSurfaceId) ?? throw new KzmException("Проектная поверхность картограммы удалена.");
            var computation = new CartogramComputation
            {
                Info = c,
                Fact = SurfaceModels.Build(database, transaction, fact, settings),
                Project = SurfaceModels.Build(database, transaction, project, settings),
                Boundary = SurfaceModels.ReadOutline(database, transaction, c.BoundaryHandle, "Граница картограммы"),
            };
            if (computation.Fact.Tin.TriangleCount == 0) throw new KzmException("Фактическая поверхность «" + fact.Name + "» не триангулируется: мало точек.");
            if (computation.Project.Tin.TriangleCount == 0) throw new KzmException("Проектная поверхность «" + project.Name + "» не триангулируется: мало точек.");
            computation.Problems.AddRange(computation.Fact.Problems.Select(p => "«" + fact.Name + "»: " + p));
            computation.Problems.AddRange(computation.Project.Problems.Select(p => "«" + project.Name + "»: " + p));
            computation.Grid = GridFor(c, computation.Boundary);
            var result = CartogramCalculator.Compute(computation.Boundary, computation.Fact.Tin.ZAt, computation.Project.Tin.ZAt, computation.Grid);
            computation.Result = result;
            computation.Problems.AddRange(result.Problems);
            if (c.ControlCheck)
            {
                try
                {
                    computation.Control = ControlVolume.Compute(computation.Boundary, computation.Fact.Tin, computation.Project.Tin);
                    if (computation.Control.MissingArea > 0)
                        computation.Problems.Add("контрольный расчёт: на площади " + ElevationFormat.Format(computation.Control.MissingArea, 1) +
                                                 " м² внутри границы нет данных хотя бы одной поверхности");
                }
                catch (KzmException exception)
                {
                    computation.Problems.Add(exception.Message);
                }
            }
            foreach (var square in result.Squares.Where(s => !s.Computed))
                computation.Marks.Add(("квадрат " + CartogramExport.ColumnLabel(c, result, square.I) + "/" + CartogramExport.RowLabel(c, result, square.J) +
                                       ": " + square.Problem, square.LabelPoint));
            foreach (var node in result.Nodes.Values.Where(n => n.Used && !n.Working.HasValue))
                computation.Marks.Add(("узел без данных " + node.Position, node.Position));
            computation.Fingerprint = FingerprintOf(database, transaction, c, surfaces,
                id => id == fact.Id ? computation.Fact.Signature : computation.Project.Signature);
            return computation;
        }

        /// <summary>Several lines for the panel and the command line.</summary>
        internal static List<string> Summary(CartogramComputation computation)
        {
            var c = computation.Info;
            var r = computation.Result;
            var lines = new List<string>();
            double balance = r.TotalFill - r.TotalCut;
            int dec = c.VolumeDecimals;
            lines.Add("Насыпь " + Volume(r.TotalFill, dec, true) + " м³, выемка " + Volume(r.TotalCut, dec, false) + " м³; баланс (насыпь − выемка) " +
                      (Math.Abs(balance) < 0.5 * Math.Pow(10, -dec) ? "0 м³" : ElevationFormat.FormatSigned(balance, dec) + " м³ — " + (balance > 0 ? "недостаток грунта" : "избыток грунта")) + ".");
            lines.Add("Квадратов " + r.Squares.Count + " (полных " + r.Squares.Count(s => s.Full) + ", неполных " + r.Squares.Count(s => !s.Full) +
                      "); площадь " + ElevationFormat.Format(r.ComputedArea, 1) + " м² из " + ElevationFormat.Format(r.BoundaryArea, 1) + " м².");
            if (computation.Control != null)
            {
                var fill = ControlVolumeResult.Difference(r.TotalFill, computation.Control.Fill);
                var cut = ControlVolumeResult.Difference(r.TotalCut, computation.Control.Cut);
                string Percent(double? value) => value.HasValue ? " (" + ElevationFormat.Format(value.Value, 2) + " %)" : string.Empty;
                lines.Add("Контрольный расчёт (наложение поверхностей): насыпь " + ElevationFormat.Format(computation.Control.Fill, 1) + ", выемка " +
                          ElevationFormat.Format(computation.Control.Cut, 1) + " м³; расхождение насыпи " + ElevationFormat.FormatSigned(fill.Cubic, 1) + " м³" +
                          Percent(fill.Percent) + ", выемки " + ElevationFormat.FormatSigned(cut.Cubic, 1) + " м³" + Percent(cut.Percent) + ".");
            }
            lines.Add(r.Balanced ? "Проверка: Σ по столбцам = Σ по рядам = итог — сходится." : "ВНИМАНИЕ: суммы по столбцам и рядам не сходятся.");
            if (computation.MovedLabels > 0) lines.Add("Подписей, поставленных на место, сдвинутое вручную: " + computation.MovedLabels + ".");
            return lines;
        }

        private static bool IsTagged(Entity entity, string cartogramId, out bool statement, out TypedValue[] values)
        {
            statement = false;
            values = null;
            using (var xdata = entity.GetXDataForApplication(App))
            {
                if (xdata == null) return false;
                values = xdata.AsArray();
                if (values.Length < 2 || !Equals(values[1].Value, cartogramId)) return false;
                statement = values.Length > 2 && Equals(values[2].Value, StatementMark);
                return true;
            }
        }

        /// <summary>Erases the drawing of the cartogram; the statement tables are kept (they hold the designer's rows).</summary>
        internal static int Erase(Database database, Transaction transaction, string cartogramId)
        {
            int count = 0;
            var tracked = KzmStore.ReadCartogramObjects(database, transaction, cartogramId);
            if (tracked != null)
            {
                var ours = new HashSet<string>(StringComparer.Ordinal) { cartogramId };
                foreach (var id in tracked)
                {
                    if (!id.IsValid || id.IsErased || !(transaction.GetObject(id, OpenMode.ForRead) is Entity entity) || entity is Table) continue;
                    bool tagged = IsTagged(entity, cartogramId, out bool statement, out _);
                    if (statement) continue;
                    if (!tagged && !CheckMarks.Targets(entity, ours)) continue;
                    EraseEntity(database, transaction, entity);
                    count++;
                }
                KzmStore.DeleteCartogramObjects(database, transaction, cartogramId);
                return count;
            }
            // Drawn by version 0.4: look through the drawing once.
            var blocks = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId blockId in blocks)
            {
                var block = (BlockTableRecord)transaction.GetObject(blockId, OpenMode.ForRead);
                if (!block.IsLayout) continue;
                foreach (ObjectId id in block)
                {
                    string type = id.ObjectClass.DxfName;
                    if (type != "LINE" && type != "TEXT" && type != "LWPOLYLINE" && type != "HATCH") continue;
                    var entity = (Entity)transaction.GetObject(id, OpenMode.ForRead);
                    if (!IsTagged(entity, cartogramId, out bool statement, out _) || statement) continue;
                    EraseEntity(database, transaction, entity);
                    count++;
                }
            }
            count += CheckMarks.Clear(database, transaction, new HashSet<string>(StringComparer.Ordinal) { cartogramId });
            return count;
        }

        private static void EraseEntity(Database database, Transaction transaction, Entity entity)
        {
            if (!Layers.IsUsable(database, transaction, entity.Layer, out string problem))
                throw new KzmException("картограмма: " + problem + ". Разблокируйте слой.");
            entity.UpgradeOpen();
            entity.Erase();
        }

        /// <summary>Labels the user moved: key «роль:i:j» → displacement from where the module put them.</summary>
        private static Dictionary<string, Vector3d> MovedLabels(Database database, Transaction transaction, string cartogramId)
        {
            var result = new Dictionary<string, Vector3d>(StringComparer.Ordinal);
            var tracked = KzmStore.ReadCartogramObjects(database, transaction, cartogramId);
            if (tracked == null) return result;
            foreach (var id in tracked)
            {
                if (!id.IsValid || id.IsErased || !(transaction.GetObject(id, OpenMode.ForRead) is DBText text)) continue;
                if (!IsTagged(text, cartogramId, out _, out var values) || values.Length < 5 || !Equals(values[2].Value, LabelMark)) continue;
                if (!(values[3].Value is string key) || !(values[4].Value is Point3d placed)) continue;
                var offset = Sources.TextAnchor(text) - placed;
                if (offset.Length > 1e-6) result[key] = offset;
            }
            return result;
        }

        /// <summary>Расчёт и построение (пересчёт): старые объекты картограммы заменяются новыми. Одна отмена.</summary>
        internal static CartogramComputation Build(Document document, string cartogramId, KzmSettings settings)
        {
            var database = document.Database;
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var c = KzmStore.ReadCartograms(database, transaction).FirstOrDefault(x => x.Id == cartogramId)
                        ?? throw new KzmException("Картограмма не найдена.");
                var computation = Compute(database, transaction, c, settings);
                var moved = c.KeepMovedLabels ? MovedLabels(database, transaction, c.Id) : new Dictionary<string, Vector3d>();
                Erase(database, transaction, c.Id);
                var painter = new Painter(database, transaction, computation, moved);
                painter.Draw();
                computation.MovedLabels = painter.Moved;
                KzmStore.WriteCartogramObjects(database, transaction, c.Id, painter.Created);
                c.Built = true;
                c.BuiltFingerprint = computation.Fingerprint;
                c.LastFill = computation.Result.TotalFill;
                c.LastCut = computation.Result.TotalCut;
                c.LastControlFill = computation.Control?.Fill;
                c.LastControlCut = computation.Control?.Cut;
                c.LastArea = computation.Result.ComputedArea;
                c.LastSquares = computation.Result.Squares.Count;
                c.BuiltAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                KzmStore.WriteCartogram(database, transaction, c);
                UpdateStatements(database, transaction, c, computation.Problems);
                transaction.Commit();
                return computation;
            }
        }

        internal static void Delete(Database database, string cartogramId, bool eraseDrawing)
        {
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                if (eraseDrawing) Erase(database, transaction, cartogramId);
                KzmStore.DeleteCartogramObjects(database, transaction, cartogramId);
                KzmStore.DeleteCartogramObjects(database, transaction, cartogramId + StatementsSuffix);
                KzmStore.DeleteCartogram(database, transaction, cartogramId);
                transaction.Commit();
            }
        }

        internal static void EraseDrawing(Database database, string cartogramId)
        {
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                Erase(database, transaction, cartogramId);
                var c = KzmStore.ReadCartograms(database, transaction).FirstOrDefault(x => x.Id == cartogramId);
                if (c != null)
                {
                    c.Built = false;
                    KzmStore.WriteCartogram(database, transaction, c);
                }
                transaction.Commit();
            }
        }

        /// <summary>Objects of the built cartogram (for «Показать на чертеже»).</summary>
        internal static List<ObjectId> ObjectsOf(Database database, Transaction transaction, string cartogramId)
        {
            var tracked = KzmStore.ReadCartogramObjects(database, transaction, cartogramId);
            if (tracked != null) return tracked.Where(id => id.IsValid && !id.IsErased).ToList();
            var result = new List<ObjectId>();
            var space = (BlockTableRecord)transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForRead);
            foreach (ObjectId id in space)
            {
                if (id.ObjectClass.DxfName != "LINE") continue;
                if (transaction.GetObject(id, OpenMode.ForRead) is Entity entity && IsTagged(entity, cartogramId, out bool statement, out _) && !statement)
                    result.Add(id);
            }
            return result;
        }

        /// <summary>Выгрузка в Excel или CSV (задание п. 6): узлы, квадраты, итоги, контрольный расчёт.</summary>
        internal static CartogramComputation Export(Document document, string cartogramId, string path, KzmSettings settings)
        {
            CartogramComputation computation;
            using (document.LockDocument(DocumentLockMode.Read, null, null, false))
            using (var transaction = document.Database.TransactionManager.StartOpenCloseTransaction())
            {
                var c = KzmStore.ReadCartograms(document.Database, transaction).FirstOrDefault(x => x.Id == cartogramId)
                        ?? throw new KzmException("Картограмма не найдена.");
                computation = Compute(document.Database, transaction, c, settings);
                transaction.Commit();
            }
            var report = new CartogramReport
            {
                Info = computation.Info,
                Result = computation.Result,
                FactName = computation.Fact.Surface.Name,
                ProjectName = computation.Project.Surface.Name,
                Control = computation.Control,
                ElevationDecimals = settings.Decimals,
                Problems = computation.Problems,
                Created = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            };
            var sheets = CartogramExport.Sheets(report);
            string temporary = path + ".tmp";
            using (var stream = new System.IO.FileStream(temporary, System.IO.FileMode.Create, System.IO.FileAccess.Write))
            {
                if (path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) CsvWriter.Write(stream, sheets);
                else XlsxWriter.Write(stream, sheets);
            }
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            System.IO.File.Move(temporary, path);
            return computation;
        }

        /// <summary>After a rebuild: the planning-soil row of the statements of this cartogram gets the new volumes.</summary>
        private static void UpdateStatements(Database database, Transaction transaction, CartogramInfo c, List<string> problems)
        {
            var ids = KzmStore.ReadCartogramObjects(database, transaction, c.Id + StatementsSuffix);
            if (ids == null) return;
            var alive = new List<ObjectId>();
            foreach (var id in ids)
            {
                if (!id.IsValid || id.IsErased || !(transaction.GetObject(id, OpenMode.ForRead) is Table table)) continue;
                alive.Add(id);
                int row = -1;
                for (int r = 0; r < table.Rows.Count && row < 0; r++)
                    if (string.Equals((table.Cells[r, 0].TextString ?? string.Empty).Trim(), PlanningRow, StringComparison.OrdinalIgnoreCase)) row = r;
                if (row < 0 || table.Columns.Count < 3)
                {
                    problems.Add("ведомость объёмов (" + table.Handle + "): строка «" + PlanningRow + "» не найдена — объёмы в ней не обновлены");
                    continue;
                }
                if (!Layers.IsUsable(database, transaction, table.Layer, out string problem))
                {
                    problems.Add("ведомость объёмов (" + table.Handle + "): " + problem + " — объёмы не обновлены");
                    continue;
                }
                table.UpgradeOpen();
                table.Cells[row, 1].TextString = Volume(c.LastFill, c.VolumeDecimals, true).TrimStart('+');
                table.Cells[row, 2].TextString = Volume(c.LastCut, c.VolumeDecimals, false).TrimStart('-');
            }
            if (alive.Count != ids.Count) KzmStore.WriteCartogramObjects(database, transaction, c.Id + StatementsSuffix, alive);
        }

        internal static string Volume(double value, int decimals, bool fill)
        {
            double rounded = Math.Round(value, decimals, MidpointRounding.AwayFromZero);
            if (rounded == 0) return "0";
            return (fill ? "+" : "-") + ElevationFormat.Format(rounded, decimals);
        }

        /// <summary>
        /// Ведомость объёмов земляных масс по форме рис. 9 СТБ 2073-2010: строку «Грунт планировки
        /// территории» заполняет модуль, остальные строки — проектировщик (значения не подставляются).
        /// </summary>
        internal static void InsertStatement(Document document, string cartogramId)
        {
            var database = document.Database;
            CartogramInfo c;
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                c = KzmStore.ReadCartograms(database, transaction).FirstOrDefault(x => x.Id == cartogramId) ?? throw new KzmException("Картограмма не найдена.");
                transaction.Commit();
            }
            if (!c.Built) throw new KzmException("Сначала постройте картограмму «" + c.Name + "».");
            var pick = document.Editor.GetPoint(new PromptPointOptions("\nУкажите левый верхний угол ведомости объёмов земляных масс: "));
            if (pick.Status != PromptStatus.OK) return;
            var at = pick.Value.TransformBy(document.Editor.CurrentUserCoordinateSystem);
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                Layers.Ensure(database, transaction, Layers.Totals);
                PointBlocks.EnsureRegApp(database, transaction, App);
                var space = (BlockTableRecord)transaction.GetObject(database.CurrentSpaceId, OpenMode.ForWrite);
                double k = c.UnitsPerMm;
                string[] names =
                {
                    PlanningRow,
                    "Вытесненный грунт, всего",
                    "в том числе при устройстве:\nа) подземных частей зданий (сооружений)",
                    "б) автодорожных покрытий",
                    "в) железнодорожных путей",
                    "г) подземных сетей",
                    "д) водоотводных сооружений",
                    "е) плодородной почвы на участках озеленения",
                    "Грунт для засыпки существующих канав, котлованов, образуемых от разбираемых подземных частей зданий (сооружений)",
                    "Грунт для устройства земляного полотна автодорог, железнодорожных путей",
                    "Грунт для устройства высоких полов зданий и обвалований сооружений",
                    "Поправка на уплотнение",
                    "Пригодный грунт, всего",
                    "Недостаток (избыток) пригодного грунта",
                    "Грунт, непригодный для устройства насыпи оснований зданий (сооружений) и подлежащий удалению с территории",
                    "Плодородный грунт, всего",
                    "в том числе:\nа) используемый для озеленения территории",
                    "б) недостаток (избыток) плодородного грунта",
                    "Итого перерабатываемого грунта",
                };
                var table = new Table { TableStyle = database.Tablestyle, Position = new Point3d(at.X, at.Y, 0), Layer = Layers.Totals };
                // All rows are data rows: the head of the form is built by merging cells below.
#pragma warning disable CS0618 // obsolete since 2008, still supported by AutoCAD 2021–2026 (checked by compiling all six targets)
                table.IsTitleSuppressed = true;
                table.IsHeaderSuppressed = true;
#pragma warning restore CS0618
                table.SetSize(3 + names.Length, 4);
                double[] widths = { 85, 20, 20, 20 };
                for (int col = 0; col < 4; col++) table.Columns[col].Width = widths[col] * k;
                table.Rows[0].Height = 5 * k;
                table.Rows[1].Height = 10 * k;
                table.Rows[2].Height = 10 * k;
                for (int row = 3; row < table.Rows.Count; row++) table.Rows[row].Height = 8 * k;
                double text = 2.5 * k;
                for (int row = 0; row < table.Rows.Count; row++)
                for (int col = 0; col < 4; col++)
                {
                    table.Cells[row, col].TextHeight = text;
                    table.Cells[row, col].Alignment = row < 3 || col > 0 ? CellAlignment.MiddleCenter : CellAlignment.MiddleLeft;
                }
                table.MergeCells(CellRange.Create(table, 0, 0, 2, 0));
                table.MergeCells(CellRange.Create(table, 0, 1, 0, 2));
                table.MergeCells(CellRange.Create(table, 1, 1, 1, 2));
                table.MergeCells(CellRange.Create(table, 0, 3, 2, 3));
                table.Cells[0, 0].TextString = "Наименование грунта";
                table.Cells[0, 1].TextString = "Количество, м³";
                table.Cells[1, 1].TextString = c.Name;
                table.Cells[2, 1].TextString = "Насыпь\n(+)";
                table.Cells[2, 2].TextString = "Выемка\n(–)";
                table.Cells[0, 3].TextString = "Примечание";
                for (int i = 0; i < names.Length; i++) table.Cells[3 + i, 0].TextString = names[i];
                table.Cells[3, 1].TextString = Volume(c.LastFill, c.VolumeDecimals, true).TrimStart('+');
                table.Cells[3, 2].TextString = Volume(c.LastCut, c.VolumeDecimals, false).TrimStart('-');
                table.Cells[3, 3].TextString = "КЗМ «" + c.Name + "»";
                table.XData = new ResultBuffer(
                    new TypedValue((int)DxfCode.ExtendedDataRegAppName, App),
                    new TypedValue((int)DxfCode.ExtendedDataAsciiString, c.Id),
                    new TypedValue((int)DxfCode.ExtendedDataAsciiString, StatementMark));
                space.AppendEntity(table);
                transaction.AddNewlyCreatedDBObject(table, true);
                table.GenerateLayout();
                var statements = KzmStore.ReadCartogramObjects(database, transaction, c.Id + StatementsSuffix) ?? new List<ObjectId>();
                statements.Add(table.ObjectId);
                KzmStore.WriteCartogramObjects(database, transaction, c.Id + StatementsSuffix, statements.Where(id => id.IsValid && !id.IsErased));
                transaction.Commit();
            }
        }

        internal static ResultBuffer Tag(string cartogramId) => new ResultBuffer(
            new TypedValue((int)DxfCode.ExtendedDataRegAppName, App),
            new TypedValue((int)DxfCode.ExtendedDataAsciiString, cartogramId));

        /// <summary>Draws one computed cartogram; every created object is listed in <see cref="Created"/>.</summary>
        private sealed class Painter
        {
            private readonly Database database;
            private readonly Transaction transaction;
            private readonly CartogramComputation computation;
            private readonly CartogramInfo c;
            private readonly GridDefinition grid;
            private readonly CartogramResult result;
            private readonly BlockTableRecord space;
            private readonly ObjectId style;
            private readonly double k;
            private readonly Dictionary<string, Vector3d> moved;

            internal Painter(Database database, Transaction transaction, CartogramComputation computation, Dictionary<string, Vector3d> moved)
            {
                this.database = database;
                this.transaction = transaction;
                this.computation = computation;
                this.moved = moved ?? new Dictionary<string, Vector3d>();
                c = computation.Info;
                grid = computation.Grid;
                result = computation.Result;
                space = (BlockTableRecord)transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForWrite);
                style = database.Textstyle;
                k = c.UnitsPerMm;
            }

            internal List<ObjectId> Created { get; } = new List<ObjectId>();
            internal int Moved { get; private set; }

            private XY W(double u, double v) => grid.ToWorld(u, v);

            private static Point3d P(XY p) => new Point3d(p.X, p.Y, 0);

            private T Add<T>(T entity, string layer) where T : Entity
            {
                entity.Layer = layer;
                entity.XData = Tag(c.Id);
                Created.Add(space.AppendEntity(entity));
                transaction.AddNewlyCreatedDBObject(entity, true);
                return entity;
            }

            private void Line(double u1, double v1, double u2, double v2, string layer) =>
                Add(new Line(P(W(u1, v1)), P(W(u2, v2))), layer);

            private void Mark(XY at, string label, double height) =>
                Created.AddRange(CheckMarks.Add(transaction, space, at, label, c.Id, height));

            /// <summary>
            /// A text in grid axes. With a <paramref name="key"/> the text remembers where the module put it,
            /// and a label the user moved before the rebuild is put at the same displacement again.
            /// </summary>
            private DBText Text(string value, double u, double v, double height, TextHorizontalMode horizontal, TextVerticalMode vertical,
                string layer, double extraRotation = 0, string key = null)
            {
                if (string.IsNullOrEmpty(value)) return null;
                var point = P(W(u, v));
                var text = new DBText
                {
                    TextString = value,
                    Height = height,
                    TextStyleId = style,
                    Rotation = grid.Angle + extraRotation,
                    HorizontalMode = horizontal,
                    VerticalMode = vertical,
                    Position = point,
                };
                if (horizontal != TextHorizontalMode.TextLeft || vertical != TextVerticalMode.TextBase) text.AlignmentPoint = point;
                Add(text, layer);
                text.AdjustAlignment(database);
                if (key == null) return text;
                text.XData = new ResultBuffer(
                    new TypedValue((int)DxfCode.ExtendedDataRegAppName, App),
                    new TypedValue((int)DxfCode.ExtendedDataAsciiString, c.Id),
                    new TypedValue((int)DxfCode.ExtendedDataAsciiString, LabelMark),
                    new TypedValue((int)DxfCode.ExtendedDataAsciiString, key),
                    new TypedValue((int)DxfCode.ExtendedDataXCoordinate, point));
                if (moved.TryGetValue(key, out var offset))
                {
                    text.TransformBy(Matrix3d.Displacement(offset));
                    Moved++;
                }
                return text;
            }

            internal void Draw()
            {
                foreach (var layer in new[] { Layers.Grid, Layers.Volumes, Layers.Totals, Layers.CutHatch }
                             .Concat(c.Labels().Select(l => l.Style.Layer)).Distinct(StringComparer.OrdinalIgnoreCase))
                    Layers.Ensure(database, transaction, layer);
                Layers.EnsureDashed(database, transaction, Layers.ZeroLine);
                Layers.Ensure(database, transaction, Layers.Check, 1, false);
                PointBlocks.EnsureRegApp(database, transaction, App);
                PointBlocks.EnsureRegApp(database, transaction, CheckMarks.AppName);
                if (result.Squares.Count == 0) throw new KzmException("Граница не пересекает ни одного квадрата сетки.");
                using (var meter = Progress.Start("КЗМ: картограмма «" + c.Name + "»", result.Squares.Count * 5))
                {
                    DrawGrid();
                    DrawHatches(meter);
                    DrawZeroLines();
                    DrawNodes(meter);
                    DrawVolumes(meter);
                    DrawColumnTotals();
                    DrawRowTotals();
                }
            }

            private void DrawGrid()
            {
                double s = grid.Step;
                double u0 = result.IMin * s, u1 = (result.IMax + 1) * s, v0 = result.JMin * s, v1 = (result.JMax + 1) * s;
                for (int i = result.IMin; i <= result.IMax + 1; i++) Line(i * s, v0, i * s, v1, Layers.Grid);
                for (int j = result.JMin; j <= result.JMax + 1; j++) Line(u0, j * s, u1, j * s, Layers.Grid);
                double th = c.TotalsTextMm * k;
                for (int i = result.IMin; i <= result.IMax; i++)
                    Text(GridLabels.Label(c.Columns, i - result.IMin + 1), (i + 0.5) * s, v1 + 0.6 * th, th,
                        TextHorizontalMode.TextCenter, TextVerticalMode.TextBottom, Layers.Grid);
                for (int j = result.JMin; j <= result.JMax; j++)
                    Text(GridLabels.Label(c.Rows, result.JMax - j + 1), u0 - 0.8 * th, (j + 0.5) * s, th,
                        TextHorizontalMode.TextRight, TextVerticalMode.TextVerticalMid, Layers.Grid);
            }

            /// <summary>
            /// Три подписи узла по шаблону картограммы (задание п. 4). По умолчанию — как в примере Б.4:
            /// рабочая — слева вверху, проектная — справа вверху, фактическая — справа внизу.
            /// </summary>
            private void DrawNodes(Progress meter)
            {
                double h = c.NodeTextMm * k;
                double g = 0.35 * h;
                foreach (var node in result.Nodes.Values.Where(n => n.Used))
                {
                    meter.Step();
                    double u = node.I * grid.Step, v = node.J * grid.Step;
                    if (!node.Working.HasValue)
                    {
                        Mark(node.Position, "нет данных", h);
                        continue;
                    }
                    Label(c.WorkingLabel, "W", node, ElevationFormat.FormatSigned(node.Working.Value, 2), u, v, h, g);
                    Label(c.ProjectLabel, "P", node, ElevationFormat.Format(node.Project.Value, 2), u, v, h, g);
                    Label(c.FactLabel, "F", node, ElevationFormat.Format(node.Fact.Value, 2), u, v, h, g);
                }
            }

            private void Label(NodeLabelStyle label, string role, CartogramNode node, string value, double u, double v, double h, double g)
            {
                bool left = label.Corner == LabelCorner.TopLeft || label.Corner == LabelCorner.BottomLeft;
                bool top = label.Corner == LabelCorner.TopLeft || label.Corner == LabelCorner.TopRight;
                var text = Text(value, u + (left ? -g : g), v + (top ? g : -g), h,
                    left ? TextHorizontalMode.TextRight : TextHorizontalMode.TextLeft,
                    top ? TextVerticalMode.TextBottom : TextVerticalMode.TextTop,
                    label.Layer, 0, role + ":" + node.I.ToString(CultureInfo.InvariantCulture) + ":" + node.J.ToString(CultureInfo.InvariantCulture));
                if (text != null && !string.IsNullOrEmpty(label.Color) && label.Color != PointAppearance.ByLayer) text.Color = PointBlocks.ParseColor(label.Color);
            }

            private void DrawVolumes(Progress meter)
            {
                double h = c.VolumeTextMm * k;
                foreach (var square in result.Squares)
                {
                    meter.Step();
                    var (u, v) = grid.ToLocal(square.LabelPoint);
                    if (!square.Computed)
                    {
                        Mark(square.LabelPoint, "не рассчитан", h);
                        continue;
                    }
                    string fill = Volume(square.Fill, c.VolumeDecimals, true);
                    string cut = Volume(square.Cut, c.VolumeDecimals, false);
                    bool hasFill = fill != "0", hasCut = cut != "0";
                    string key = ":" + square.I.ToString(CultureInfo.InvariantCulture) + ":" + square.J.ToString(CultureInfo.InvariantCulture);
                    if (hasFill && hasCut)
                    {
                        Text(fill, u, v + 0.15 * h, h, TextHorizontalMode.TextCenter, TextVerticalMode.TextBottom, Layers.Volumes, 0, "V+" + key);
                        Text(cut, u, v - 0.15 * h, h, TextHorizontalMode.TextCenter, TextVerticalMode.TextTop, Layers.Volumes, 0, "V-" + key);
                    }
                    else
                    {
                        Text(hasCut ? cut : fill, u, v, h, TextHorizontalMode.TextCenter, TextVerticalMode.TextVerticalMid, Layers.Volumes, 0, "V" + key);
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

            /// <summary>Площадь выемок — штриховкой под углом 45° (СТБ 2073-2010, п. 7.1 б).</summary>
            private void DrawHatches(Progress meter)
            {
                foreach (var square in result.Squares.Where(s => s.Computed && s.CutPolygons.Count > 0))
                {
                    meter.Step();
                    var hatch = Add(new Hatch(), Layers.CutHatch);
                    try
                    {
                        hatch.SetHatchPattern(HatchPatternType.PreDefined, "ANSI31");
                        hatch.PatternScale = k;
                        hatch.Associative = false;
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
                        computation.Problems.Add("штриховка выемки в квадрате " + GridLabels.Label(c.Columns, square.I - result.IMin + 1) + "/" +
                                                 GridLabels.Label(c.Rows, result.JMax - square.J + 1) + " не построена");
                    }
                }
            }

            /// <summary>Таблица под сеткой по форме рис. 8 СТБ 2073-2010: насыпь и выемка по столбцам и «Всего».</summary>
            private void DrawColumnTotals()
            {
                double s = grid.Step, rh = 8 * k, th = c.TotalsTextMm * k;
                double u0 = result.IMin * s, u1 = (result.IMax + 1) * s, top = result.JMin * s;
                double left = u0 - 25 * k, labelLeft = u0 - 20 * k, right = u1 + 25 * k, totalLeft = u1 + 5 * k;
                Line(left, top, right, top, Layers.Totals);
                Line(labelLeft, top - rh, u1, top - rh, Layers.Totals);
                Line(totalLeft, top - rh, right, top - rh, Layers.Totals);
                Line(left, top - 2 * rh, right, top - 2 * rh, Layers.Totals);
                foreach (double u in new[] { left, labelLeft, totalLeft, right }) Line(u, top, u, top - 2 * rh, Layers.Totals);
                for (int i = result.IMin; i <= result.IMax + 1; i++) Line(i * s, top, i * s, top - 2 * rh, Layers.Totals);
                Text("Итого, м³", (left + labelLeft) / 2, top - rh, th, TextHorizontalMode.TextCenter, TextVerticalMode.TextVerticalMid, Layers.Totals, Math.PI / 2);
                Text("Насыпь (+)", labelLeft + 1.5 * k, top - rh / 2, th, TextHorizontalMode.TextLeft, TextVerticalMode.TextVerticalMid, Layers.Totals);
                Text("Выемка (–)", labelLeft + 1.5 * k, top - 1.5 * rh, th, TextHorizontalMode.TextLeft, TextVerticalMode.TextVerticalMid, Layers.Totals);
                for (int i = result.IMin; i <= result.IMax; i++)
                {
                    result.ColumnTotals.TryGetValue(i, out var total);
                    Text(Volume(total.Fill, c.VolumeDecimals, true), (i + 0.5) * s, top - rh / 2, th, TextHorizontalMode.TextCenter, TextVerticalMode.TextVerticalMid, Layers.Totals);
                    Text(Volume(total.Cut, c.VolumeDecimals, false), (i + 0.5) * s, top - 1.5 * rh, th, TextHorizontalMode.TextCenter, TextVerticalMode.TextVerticalMid, Layers.Totals);
                }
                Text("Всего, м³", (u1 + totalLeft) / 2, top - rh, th, TextHorizontalMode.TextCenter, TextVerticalMode.TextVerticalMid, Layers.Totals, Math.PI / 2);
                Text(Volume(result.TotalFill, c.VolumeDecimals, true), (totalLeft + right) / 2, top - rh / 2, th, TextHorizontalMode.TextCenter, TextVerticalMode.TextVerticalMid, Layers.Totals);
                Text(Volume(result.TotalCut, c.VolumeDecimals, false), (totalLeft + right) / 2, top - 1.5 * rh, th, TextHorizontalMode.TextCenter, TextVerticalMode.TextVerticalMid, Layers.Totals);
            }

            /// <summary>Итоги по рядам — справа от сетки (задание, п. 5).</summary>
            private void DrawRowTotals()
            {
                double s = grid.Step, hh = 8 * k, th = c.TotalsTextMm * k, w = 20 * k;
                double u1 = (result.IMax + 1) * s, top = (result.JMax + 1) * s, bottom = result.JMin * s;
                Line(u1, top + hh, u1 + 2 * w, top + hh, Layers.Totals);
                Line(u1, top + hh, u1, top, Layers.Totals);
                foreach (double u in new[] { u1 + w, u1 + 2 * w }) Line(u, top + hh, u, bottom, Layers.Totals);
                for (int j = result.JMin; j <= result.JMax + 1; j++) Line(u1, j * s, u1 + 2 * w, j * s, Layers.Totals);
                Text("Насыпь (+)", u1 + w / 2, top + hh / 2, th, TextHorizontalMode.TextCenter, TextVerticalMode.TextVerticalMid, Layers.Totals);
                Text("Выемка (–)", u1 + 1.5 * w, top + hh / 2, th, TextHorizontalMode.TextCenter, TextVerticalMode.TextVerticalMid, Layers.Totals);
                for (int j = result.JMin; j <= result.JMax; j++)
                {
                    result.RowTotals.TryGetValue(j, out var total);
                    Text(Volume(total.Fill, c.VolumeDecimals, true), u1 + w / 2, (j + 0.5) * s, th, TextHorizontalMode.TextCenter, TextVerticalMode.TextVerticalMid, Layers.Totals);
                    Text(Volume(total.Cut, c.VolumeDecimals, false), u1 + 1.5 * w, (j + 0.5) * s, th, TextHorizontalMode.TextCenter, TextVerticalMode.TextVerticalMid, Layers.Totals);
                }
            }
        }
    }
}
