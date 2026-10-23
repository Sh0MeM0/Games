using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Profiles.Core;

namespace Profiles.AutoCAD
{
    /// <summary>Read-only, command-context survey capture. Nothing is written before transfer confirmation.</summary>
    internal static class DrawingCapture
    {
        private sealed class CaptureCancelledException : OperationCanceledException { }

        internal static DrawingCaptureResult Run(Document document, DrawingCaptureRequest request)
        {
            if (request == null || request.Draft == null) throw new ProfileValidationException("Нет проекта для набора точек.");
            var editor = document.Editor;
            var options = (request.Options ?? new DrawingCaptureOptions()).Clone();
            var cancelled = new DrawingCaptureResult
            {
                Project = request.Draft, SelectedSectionId = request.SelectedSectionId,
                Options = request.Options == null ? new DrawingCaptureOptions() : request.Options.Clone()
            };
            try
            {
                var units = SourceUnits(editor, options);
                if (!Finite(options.StartStation) || Math.Abs(options.StartStation) > 1e9)
                    throw new ProfileValidationException("Начальный пикетаж должен быть конечным числом в пределах ±1000000000 м.");
                editor.WriteMessage("\nНабор профиля: точки задают положение и расстояния XY; щелчки по подписям расстояния не меняют." +
                    "\nОтметку можно ввести числом, выбрать через Текст или прочитать через Z. Enter пропускает отметку; Esc отменяет весь текущий набор.");
                editor.WriteMessage("\nИсточник: {0:G17} м в 1 единице чертежа. Масштаб оформления профиля на расстояния не влияет.", units.MetresPerDrawingUnit);
                ProfileProject imported;
                string selectedId = request.SelectedSectionId;
                int rows;
                if (request.Kind == ProfileKind.Longitudinal)
                {
                    var picks = Longitudinal(editor, request.Draft, units, options.StartStation);
                    if (picks.Count == 0) return cancelled;
                    var points = DrawingSurvey.BuildLongitudinal(picks, options.StartStation, units);
                    imported = DrawingSurvey.MergeLongitudinal(request.Draft, points);
                    rows = points.Count;
                    editor.WriteMessage("\nНабрано продольных точек: {0}. Пикетаж: {1:F2} — {2:F2} м.", rows, points[0].Station, points[rows - 1].Station);
                    editor.WriteMessage("\nСовпадающие пикетажи дополнят существующие строки; пропущенные отметки не стирают прежние значения.");
                }
                else
                {
                    var sections = CrossSections(editor, request.Draft, units);
                    if (sections.Count == 0) return cancelled;
                    imported = DrawingSurvey.AppendSections(request.Draft, sections);
                    selectedId = sections[0].SectionId;
                    rows = sections.Sum(section => section.Points.Count);
                    editor.WriteMessage("\nНабрано поперечников: {0}; точек: {1}. Пикетажи и роли точек можно назначить в окне профилей.", sections.Count, rows);
                }
                var transfer = new PromptKeywordOptions("\nПеренести набранные данные в проект? [Да/Нет] <Да>: ", "Yes No")
                { AllowNone = true };
                transfer.Keywords.Default = "Yes";
                var answer = editor.GetKeywords(transfer);
                ThrowIfCancelled(answer.Status);
                if (answer.Status == PromptStatus.Keyword || answer.Status == PromptStatus.OK)
                {
                    if (!string.Equals(answer.StringResult, "Yes", StringComparison.OrdinalIgnoreCase))
                    {
                        editor.WriteMessage("\nПеренос отменён. Исходные данные проекта сохранены.");
                        return cancelled;
                    }
                }
                else if (answer.Status != PromptStatus.None) throw new ProfileValidationException("Не удалось подтвердить перенос данных.");
                return new DrawingCaptureResult { Accepted = true, Project = imported, SelectedSectionId = selectedId, Options = options };
            }
            catch (CaptureCancelledException)
            {
                editor.WriteMessage("\nНабор отменён. Ранее введённый проект не изменён.");
                return cancelled;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception exception)
                when (exception.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.UserBreak)
            {
                editor.WriteMessage("\nНабор отменён. Ранее введённый проект не изменён.");
                return cancelled;
            }
        }

        private static SurveySourceUnits SourceUnits(Editor editor, DrawingCaptureOptions options)
        {
            SurveySourceUnits units;
            switch (options.SourceMode)
            {
                case DrawingCaptureSourceMode.ModelMetres: units = SurveySourceUnits.ModelMetres(); break;
                case DrawingCaptureSourceMode.PaperMillimetres: units = SurveySourceUnits.PaperMillimetres(options.PaperScale); break;
                case DrawingCaptureSourceMode.Custom: units = SurveySourceUnits.Custom(options.MetresPerDrawingUnit); break;
                case DrawingCaptureSourceMode.Calibration:
                    editor.WriteMessage("\nКалибровка расстояний источника: две точки и известная длина в метрах.");
                    var first = PickPosition(editor, "\nПервая точка калибровки (Enter — отменить набор): ", null);
                    if (!first.HasValue) throw new CaptureCancelledException();
                    var second = PickPosition(editor, "\nВторая точка калибровки (Enter — отменить набор): ", first);
                    if (!second.HasValue) throw new CaptureCancelledException();
                    var real = editor.GetDouble(new PromptDoubleOptions("\nИзвестное расстояние между точками, м: ")
                    { AllowNone = false, AllowNegative = false, AllowZero = false });
                    ThrowIfCancelled(real.Status);
                    if (real.Status != PromptStatus.OK) throw new ProfileValidationException("Не задано расстояние калибровки.");
                    units = SurveySourceUnits.Calibrated(XY(first.Value), XY(second.Value), real.Value);
                    break;
                default: throw new ProfileValidationException("Неизвестный способ пересчёта расстояний.");
            }
            options.MetresPerDrawingUnit = units.MetresPerDrawingUnit;
            return units;
        }

        private static List<LongitudinalSurveyPick> Longitudinal(Editor editor, ProfileProject project, SurveySourceUnits units, double startStation)
        {
            // Diagnose ambiguous existing stations before asking the user to
            // collect data. The budget includes every inactive profile as well.
            ProjectSynchronization.SortLongitudinal(project.Longitudinal);
            var capacity = DrawingSurveyCapacity.FromProject(project);
            var result = new List<LongitudinalSurveyPick>();
            Point3d? previous = null;
            double station = startStation;
            while (result.Count < 10000)
            {
                string prompt = result.Count == 0
                    ? "\nНачальная точка оси, ПК " + Station(startStation) + " (Enter — закончить набор): "
                    : "\nСледующая точка оси (Enter — закончить набор): ";
                var point = PickPosition(editor, prompt, previous);
                if (!point.HasValue) break;
                double step = previous.HasValue ? PlanDistance(previous.Value, point.Value) * units.MetresPerDrawingUnit : 0;
                if (previous.HasValue && step == 0) { editor.WriteMessage("\nЭта точка совпадает с предыдущей в плане XY. Укажите другую точку."); continue; }
                if (!Finite(step) || !Finite(station + step) || Math.Abs(station + step) > 1e9)
                { editor.WriteMessage("\nСуммарный пикетаж выходит за допустимый диапазон. Укажите другую точку."); continue; }
                double nextStation = station + step;
                if (previous.HasValue && nextStation <= station)
                { editor.WriteMessage("\nРасстояние слишком мало для заданного пикетажа. Укажите другую точку или проверьте коэффициент единиц."); continue; }
                if (!capacity.TryAddLongitudinalStation(nextStation))
                {
                    editor.WriteMessage("\nДостигнут общий предел 10000 точек во всех профилях проекта. Новая точка не добавлена; завершённые точки можно перенести.");
                    break;
                }
                station = nextStation;
                editor.WriteMessage("\nПК {0}; расстояние от предыдущей точки {1:F3} м.", Station(station), step);
                var values = new LongitudinalPoint();
                if (project.Template == ProfileTemplate.Reconstruction)
                {
                    values.LeftBank = Elevation(editor, "Левый берег", units);
                    values.RightBank = Elevation(editor, "Правый берег", units);
                    values.ExistingBottom = Elevation(editor, "Существующее дно", units);
                    values.ProjectBottom = Elevation(editor, "Проектное дно", units);
                    values.Water = Elevation(editor, "Урез воды", units);
                }
                else if (project.Template == ProfileTemplate.NewChannel)
                {
                    values.Ground = Elevation(editor, "Натурная поверхность", units);
                    values.ProjectBottom = Elevation(editor, "Проектное дно", units);
                }
                else
                {
                    values.Ground = Elevation(editor, "Натурная поверхность", units);
                    values.ProjectGround = Elevation(editor, "Проектная поверхность", units);
                    values.PipeInvert = Elevation(editor, "Лоток трубы", units);
                }
                result.Add(new LongitudinalSurveyPick { Position = XY(point.Value), Values = values });
                previous = point;
            }
            if (result.Count == 10000) editor.WriteMessage("\nДостигнут предел 10000 точек; набор завершён.");
            return result;
        }

        private static List<CrossSection> CrossSections(Editor editor, ProfileProject project, SurveySourceUnits units)
        {
            var result = new List<CrossSection>();
            var capacity = DrawingSurveyCapacity.FromProject(project);
            int remaining = capacity.RemainingPoints;
            int sectionLimit = capacity.RemainingSections;
            if (remaining < 1 || sectionLimit < 1) throw new ProfileValidationException("В проекте недостаточно места для нового поперечника: предел 200 поперечников и 10000 точек.");
            while (result.Count < sectionLimit && remaining > 0)
            {
                int number = project.Sections.Count + result.Count + 1;
                var first = PickPosition(editor, "\nПервая точка нового поперечника " + number + " (Enter — закончить все поперечники): ", null);
                if (!first.HasValue) break;
                var picks = new List<SectionSurveyPick>();
                Point3d? current = first, previous = null;
                double offset = 0;
                while (current.HasValue && picks.Count < remaining)
                {
                    editor.WriteMessage("\nПоперечник {0}, точка {1}.", number, picks.Count + 1);
                    picks.Add(new SectionSurveyPick
                    {
                        Position = XY(current.Value),
                        Values = new SectionPoint
                        {
                            Existing = Elevation(editor, "Натурная отметка", units),
                            Design = Elevation(editor, "Проектная отметка", units)
                        }
                    });
                    previous = current;
                    if (picks.Count == remaining) break;
                    do
                    {
                        current = PickPosition(editor, "\nСледующая точка поперечника (Enter — закончить этот поперечник): ", previous);
                        if (current.HasValue && PlanDistance(previous.Value, current.Value) == 0)
                            editor.WriteMessage("\nПовтор той же точки XY не добавлен. Укажите другую точку.");
                        else if (current.HasValue)
                        {
                            double step = PlanDistance(previous.Value, current.Value) * units.MetresPerDrawingUnit;
                            if (!Finite(step) || !Finite(offset + step) || offset + step > 1e9 || offset + step <= offset)
                                editor.WriteMessage("\nСуммарная ширина промера выходит за допустимый диапазон. Укажите другую точку.");
                            else { offset += step; break; }
                        }
                        else break;
                    } while (true);
                }
                var section = DrawingSurvey.BuildSection(picks, units, null, "Промер " + number);
                section.Water = Elevation(editor, "Общий урез воды для этого поперечника", units);
                result.Add(section);
                remaining -= picks.Count;
                editor.WriteMessage("\nПоперечник {0} принят в набор: {1} точек. Укажите начало следующего или нажмите Enter.", number, picks.Count);
            }
            if (result.Count == sectionLimit || remaining == 0) editor.WriteMessage("\nДостигнут предел проекта; набор завершён.");
            return result;
        }

        private static double? Elevation(Editor editor, string caption, SurveySourceUnits units)
        {
            while (true)
            {
                var prompt = new PromptDoubleOptions("\n" + caption + ", м [Текст/Z] <пропустить>: ", "Text Z")
                { AllowNone = true, AllowNegative = true, AllowZero = true, AllowArbitraryInput = false };
                var answer = editor.GetDouble(prompt);
                ThrowIfCancelled(answer.Status);
                if (answer.Status == PromptStatus.None) return null;
                if (answer.Status != PromptStatus.OK && answer.Status != PromptStatus.Keyword)
                    throw new ProfileValidationException("AutoCAD не смог получить отметку; набор не перенесён.");
                try
                {
                    if (answer.Status == PromptStatus.OK) return CheckedElevation(answer.Value);
                    if (answer.Status == PromptStatus.Keyword && string.Equals(answer.StringResult, "Text", StringComparison.OrdinalIgnoreCase))
                        return TextElevation(editor);
                    if (answer.Status == PromptStatus.Keyword && string.Equals(answer.StringResult, "Z", StringComparison.OrdinalIgnoreCase))
                    {
                        var point = PickPosition(editor, "\nТочка для чтения Z (Enter — пропустить отметку): ", null);
                        return point.HasValue ? CheckedElevation(point.Value.Z * units.MetresPerDrawingUnit) : (double?)null;
                    }
                    throw new ProfileValidationException("Введите число либо выберите Текст или Z.");
                }
                catch (ProfileValidationException exception) { editor.WriteMessage("\n" + exception.Message); }
            }
        }

        private static double? TextElevation(Editor editor)
        {
            while (true)
            {
                var selected = editor.GetNestedEntity(new PromptNestedEntityOptions("\nВыберите TEXT, MTEXT или атрибут с одной отметкой (Enter — пропустить): ") { AllowNone = true });
                ThrowIfCancelled(selected.Status);
                if (selected.Status == PromptStatus.None) return null;
                if (selected.Status != PromptStatus.OK) throw new ProfileValidationException("Не удалось выбрать подпись отметки.");
                if (selected.ObjectId.IsNull || !selected.ObjectId.IsValid || selected.ObjectId.Database == null)
                    throw new ProfileValidationException("Выбранная подпись больше недоступна. Выберите другой текст или введите число.");
                try
                {
                    string text;
                    // The read transaction is opened only AFTER the pick, and is
                    // disposed before any further prompt. Nested attributes work too.
                    using (var transaction = selected.ObjectId.Database.TransactionManager.StartOpenCloseTransaction())
                    {
                        var entity = transaction.GetObject(selected.ObjectId, OpenMode.ForRead);
                        if (entity is AttributeReference attribute && attribute.IsMTextAttribute)
                        { using (var mtext = attribute.MTextAttribute) text = mtext.Text; }
                        else if (entity is DBText plain) text = plain.TextString;
                        else if (entity is MText formatted) text = formatted.Text;
                        else throw new ProfileValidationException("Выбранный объект не является текстом или атрибутом. Выберите саму подпись.");
                    }
                    double value = ElevationTextParser.Parse(text);
                    editor.WriteMessage("\nПрочитана отметка: {0:G17} м.", value);
                    return value;
                }
                catch (ProfileValidationException exception) { editor.WriteMessage("\n" + exception.Message); }
            }
        }

        private static Point3d? PickPosition(Editor editor, string message, Point3d? previous)
        {
            while (true)
            {
                var options = new PromptPointOptions(message) { AllowNone = true };
                if (previous.HasValue)
                {
                    options.UseBasePoint = true;
                    options.BasePoint = previous.Value.TransformBy(editor.CurrentUserCoordinateSystem.Inverse());
                    options.UseDashedLine = true;
                }
                var result = editor.GetPoint(options);
                ThrowIfCancelled(result.Status);
                if (result.Status == PromptStatus.None) return null;
                if (result.Status != PromptStatus.OK) throw new ProfileValidationException("Не удалось получить точку чертежа.");
                var world = result.Value.TransformBy(editor.CurrentUserCoordinateSystem);
                if (Finite(world.X) && Finite(world.Y) && Finite(world.Z) && Math.Abs(world.X) <= 1e12 && Math.Abs(world.Y) <= 1e12 && Math.Abs(world.Z) <= 1e12)
                    return world;
                editor.WriteMessage("\nКоординаты должны быть конечными и находиться в пределах ±1000000000000 единиц.");
            }
        }

        private static double CheckedElevation(double value)
        {
            if (!Finite(value) || Math.Abs(value) > 1e6) throw new ProfileValidationException("Отметка должна быть конечным числом в пределах ±1000000 м.");
            return value;
        }
        private static void ThrowIfCancelled(PromptStatus status) { if (status == PromptStatus.Cancel) throw new CaptureCancelledException(); }
        private static Point2 XY(Point3d point) => new Point2(point.X, point.Y);
        private static double PlanDistance(Point3d a, Point3d b) => Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private static string Station(double value)
        {
            double shown = Math.Round(value, 2, MidpointRounding.AwayFromZero);
            if (shown < 0) return shown.ToString("F2", CultureInfo.InvariantCulture);
            double picket = Math.Floor(shown / 100);
            return picket.ToString("0", CultureInfo.InvariantCulture) + "+" + (shown - picket * 100).ToString("00.00", CultureInfo.InvariantCulture);
        }
    }
}
