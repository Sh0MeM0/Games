namespace Profiles.Core
{
    /// <summary>Independent scales for each profile kind; other drawing settings remain shared.</summary>
    public static class ProfileScales
    {
        public static double GetHorizontal(ProfileSettings settings, ProfileKind kind)
        {
            CheckKind(kind);
            Validate(settings);
            return kind == ProfileKind.CrossSection ? settings.CrossHorizontalScale ?? settings.HorizontalScale : settings.HorizontalScale;
        }

        public static double GetVertical(ProfileSettings settings, ProfileKind kind)
        {
            CheckKind(kind);
            Validate(settings);
            return kind == ProfileKind.CrossSection ? settings.CrossVerticalScale ?? settings.VerticalScale : settings.VerticalScale;
        }

        /// <summary>Changes only the requested pair, validating both values before mutation.</summary>
        public static void Set(ProfileSettings settings, ProfileKind kind, double horizontal, double vertical)
        {
            CheckKind(kind);
            if (settings == null) throw new ProfileValidationException("Настройки профиля отсутствуют.");
            Validate(settings);
            ProfileValidation.Range(horizontal, 1, 1000000, "Горизонтальный масштаб");
            ProfileValidation.Range(vertical, 1, 1000000, "Вертикальный масштаб");
            if (kind == ProfileKind.Longitudinal)
            {
                // Materialize the legacy fallback first so editing longitudinal
                // scales never changes a previously inherited cross pair.
                if (!settings.CrossHorizontalScale.HasValue && !settings.CrossVerticalScale.HasValue)
                {
                    settings.CrossHorizontalScale = settings.HorizontalScale;
                    settings.CrossVerticalScale = settings.VerticalScale;
                }
                settings.HorizontalScale = horizontal;
                settings.VerticalScale = vertical;
            }
            else
            {
                settings.CrossHorizontalScale = horizontal;
                settings.CrossVerticalScale = vertical;
            }
        }

        /// <summary>Effective settings copy for drawing; never use it to replace saved project settings.</summary>
        public static ProfileSettings ForKind(ProfileSettings settings, ProfileKind kind)
        {
            return new ProfileSettings
            {
                Title = settings == null ? null : settings.Title,
                HorizontalScale = GetHorizontal(settings, kind), VerticalScale = GetVertical(settings, kind),
                CrossHorizontalScale = settings.CrossHorizontalScale, CrossVerticalScale = settings.CrossVerticalScale,
                MetresPerDrawingUnit = settings.MetresPerDrawingUnit, Datum = settings.Datum,
                TextHeightMm = settings.TextHeightMm, TextWidthFactor = settings.TextWidthFactor,
                ShowEmptyRows = settings.ShowEmptyRows
            };
        }

        internal static void Validate(ProfileSettings settings)
        {
            if (settings == null) throw new ProfileValidationException("Настройки профиля отсутствуют.");
            ProfileValidation.Range(settings.HorizontalScale, 1, 1000000, "Горизонтальный масштаб продольного профиля");
            ProfileValidation.Range(settings.VerticalScale, 1, 1000000, "Вертикальный масштаб продольного профиля");
            if (settings.CrossHorizontalScale.HasValue != settings.CrossVerticalScale.HasValue)
                throw new ProfileValidationException("Для поперечного профиля укажите оба масштаба: горизонтальный и вертикальный.");
            if (settings.CrossHorizontalScale.HasValue)
            {
                ProfileValidation.Range(settings.CrossHorizontalScale.Value, 1, 1000000, "Горизонтальный масштаб поперечного профиля");
                ProfileValidation.Range(settings.CrossVerticalScale.Value, 1, 1000000, "Вертикальный масштаб поперечного профиля");
            }
        }

        private static void CheckKind(ProfileKind kind)
        {
            if (kind != ProfileKind.Longitudinal && kind != ProfileKind.CrossSection)
                throw new ProfileValidationException("Неизвестный тип профиля для выбора масштаба.");
        }
    }
}
