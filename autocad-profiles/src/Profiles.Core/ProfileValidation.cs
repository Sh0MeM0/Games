using System;
using System.Collections.Generic;
using System.Linq;

namespace Profiles.Core
{
    internal static class ProfileValidation
    {
        internal static void Check(ProfileProject project, bool geometry = true)
        {
            if (project == null) Fail("Проект не задан.");
            if (project.SchemaVersion != 2) Fail("Версия проекта не поддерживается: " + project.SchemaVersion + ". Требуется версия 2.");
            if (!Guid.TryParseExact(project.ProjectId, "N", out var identity) && !Guid.TryParseExact(project.ProjectId, "D", out identity)) Fail("Некорректный идентификатор проекта.");
            Text(project.Name, "Название проекта", 200);
            if (!Enum.IsDefined(typeof(ProfileKind), project.Kind)) Fail("Неизвестный тип профиля.");
            if (!Enum.IsDefined(typeof(ProfileTemplate), project.Template)) Fail("Неизвестный шаблон профиля.");
            if (project.Settings == null || project.Longitudinal == null || project.Sections == null) Fail("В проекте отсутствуют настройки или списки точек.");
            ProfileSettings settings = project.Settings;
            Text(settings.Title, "Название профиля", 200, geometry);
            Range(settings.HorizontalScale, 1, 1000000, "Горизонтальный масштаб");
            Range(settings.VerticalScale, 1, 1000000, "Вертикальный масштаб");
            Range(settings.MetresPerDrawingUnit, 0.000001, 1000000, "Метры в единице чертежа");
            Range(settings.TextHeightMm, 0.5, 20, "Высота текста, мм");
            Range(settings.TextWidthFactor, 0.1, 3, "Коэффициент ширины текста");
            Elevation(settings.Datum, "Условная отметка");
            if (project.Sections.Count > 200 || project.Longitudinal.Count > 10000) Fail("Слишком большой проект: допустимо не более 200 поперечников и 10000 точек.");
            int count = project.Longitudinal.Count;
            foreach (CrossSection section in project.Sections)
            {
                if (section == null || section.Points == null) Fail("Поперечник или список его точек отсутствует.");
                count += section.Points.Count;
                if (count > 10000) Fail("В проекте допустимо не более 10000 точек суммарно.");
                CheckSection(section, geometry && project.Kind == ProfileKind.CrossSection);
            }
            double previous = double.NegativeInfinity;
            foreach (LongitudinalPoint point in project.Longitudinal)
            {
                if (point == null) Fail("Продольный профиль содержит пустую строку вместо точки.");
                Range(point.Station, -1000000000, 1000000000, "Пикетаж");
                if (geometry && project.Kind == ProfileKind.Longitudinal && point.Station <= previous) Fail("Пикетаж должен строго возрастать; удалите совпадающие точки или исправьте порядок строк.");
                previous = point.Station;
                foreach (double? value in new[] { point.Ground, point.ExistingBottom, point.ProjectBottom, point.Water, point.LeftBank, point.RightBank, point.ProjectGround, point.PipeInvert })
                    Elevation(value, "Отметка на пикетаже " + point.Station);
                if (point.PeatDepth.HasValue) Range(point.PeatDepth.Value, 0, 1000000, "Глубина торфа");
                Text(point.PipeDescription, "Характеристика трубопровода", 200, false);
                Text(point.Foundation, "Основание трубопровода", 200, false);
                Text(point.PointName, "Обозначение точки", 100, false);
                Positive(point.BottomWidth, "Ширина дна");
                Positive(point.LeftSlope, "Левое заложение откоса");
                Positive(point.RightSlope, "Правое заложение откоса");
            }
            if (geometry && project.Kind == ProfileKind.Longitudinal && project.Longitudinal.Count < 2) Fail("Для продольного профиля введите не менее двух точек с возрастающим пикетажем.");
            if (geometry && project.Kind == ProfileKind.CrossSection && project.Sections.Count == 0) Fail("Добавьте хотя бы один поперечник.");
        }

        private static void CheckSection(CrossSection section, bool active)
        {
            Text(section.Name, "Обозначение поперечника", 100, active);
            if (active && section.Points.Count < 2) Fail("Поперечник «" + section.Name + "»: нужно не менее двух точек.");
            if (section.Station.HasValue) Range(section.Station.Value, -1000000000, 1000000000, "Пикетаж поперечника");
            Range(section.CenterOffset, -1000000000, 1000000000, "Смещение оси");
            Elevation(section.LeftBank, "Отметка левого берега");
            Elevation(section.RightBank, "Отметка правого берега");
            Elevation(section.ExistingBottom, "Отметка существующего дна");
            Elevation(section.Water, "Уровень воды");
            Elevation(section.ProjectBottom, "Проектная отметка дна");
            Positive(section.BottomWidth, "Ширина дна");
            Positive(section.LeftSlope, "Левое заложение откоса");
            Positive(section.RightSlope, "Правое заложение откоса");
            int supplied = new[] { section.ProjectBottom, section.BottomWidth, section.LeftSlope, section.RightSlope }.Count(x => x.HasValue);
            if (active && supplied != 0 && supplied != 4) Fail("Поперечник «" + section.Name + "»: для трапеции нужны одновременно отметка дна, ширина и оба заложения откосов.");
            if (active && supplied != 0 && section.Points.Any(p => p != null && p.Design.HasValue)) Fail("Поперечник «" + section.Name + "»: выберите трапецию или проектные отметки в таблице; одновременно применять их нельзя.");
            double previous = double.NegativeInfinity;
            foreach (SectionPoint point in section.Points)
            {
                if (point == null) Fail("Поперечник «" + section.Name + "» содержит пустую строку вместо точки.");
                Range(point.Offset, -1000000000, 1000000000, "Смещение точки поперечника");
                if (active && point.Offset <= previous) Fail("Поперечник «" + section.Name + "»: смещения должны строго возрастать слева направо; совпадающие точки недопустимы.");
                previous = point.Offset;
                Elevation(point.Existing, "Натурная отметка");
                Elevation(point.Design, "Проектная отметка");
            }
            if (active && !section.Points.Any(p => p.Existing.HasValue || p.Design.HasValue)) Fail("Поперечник «" + section.Name + "»: введите хотя бы одну натурную или проектную отметку.");
        }

        internal static void Elevation(double? value, string field) { if (value.HasValue) Range(value.Value, -1000000, 1000000, field); }
        private static void Positive(double? value, string field) { if (value.HasValue) Range(value.Value, 0.000001, 1000000, field); }
        internal static void Range(double value, double min, double max, string field)
        {
            if (!NumberParser.Finite(value) || value < min || value > max)
                Fail(field + ": допустимо конечное значение от " + min + " до " + max + ".");
        }
        private static void Text(string value, string field, int max, bool required = true)
        {
            if (value == null || (required && string.IsNullOrWhiteSpace(value)) || value.Length > max || value.Any(c => char.IsControl(c)))
                Fail(field + ": введите от 1 до " + max + " символов без управляющих знаков.");
        }
        internal static void Fail(string message) { throw new ProfileValidationException(message); }
    }
}
