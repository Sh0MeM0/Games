using System;
using Profiles.Core;

namespace Profiles.AutoCAD
{
    public sealed partial class ProfilesForm
    {
        // The controls show the active kind; this buffer retains both complete
        // precision pairs when the user switches tabs or opens the preview.
        private ProfileSettings scaleState = new ProfileSettings
        {
            HorizontalScale = 1000, VerticalScale = 100,
            CrossHorizontalScale = 100, CrossVerticalScale = 100
        };

        private void LoadProfileScales(ProfileSettings settings)
        {
            scaleState = new ProfileSettings
            {
                HorizontalScale = ProfileScales.GetHorizontal(settings, ProfileKind.Longitudinal),
                VerticalScale = ProfileScales.GetVertical(settings, ProfileKind.Longitudinal),
                CrossHorizontalScale = ProfileScales.GetHorizontal(settings, ProfileKind.CrossSection),
                CrossVerticalScale = ProfileScales.GetVertical(settings, ProfileKind.CrossSection)
            };
            DisplayActiveScales();
        }

        private void CaptureActiveScales()
        {
            double horizontalValue = ReadRequiredNumber(horizontal, "Горизонтальный масштаб");
            double verticalValue = ReadRequiredNumber(vertical, "Вертикальный масштаб");
            ProfileScales.Set(scaleState, activeKind, horizontalValue, verticalValue);
        }

        private void DisplayActiveScales()
        {
            bool oldLoading = loading;
            loading = true;
            try
            {
                DisplayNumber(horizontal, ProfileScales.GetHorizontal(scaleState, activeKind));
                DisplayNumber(vertical, ProfileScales.GetVertical(scaleState, activeKind));
                string profile = activeKind == ProfileKind.Longitudinal ? "продольного профиля" : "поперечных профилей";
                tips.SetToolTip(horizontal, "Горизонтальный масштаб " + profile + ". Масштаб другого вида профиля сохраняется отдельно.");
                tips.SetToolTip(vertical, "Вертикальный масштаб " + profile + ". Масштаб другого вида профиля сохраняется отдельно.");
            }
            finally { loading = oldLoading; }
        }

        private bool TrySwitchProfileKind(ProfileKind next)
        {
            try
            {
                // Validate both outgoing numbers before switching: unfinished
                // text remains visible and neither stored pair is changed.
                CaptureActiveScales();
                activeKind = next;
                DisplayActiveScales();
                return true;
            }
            catch (ProfileValidationException error)
            {
                bool oldLoading = loading;
                loading = true;
                try { tabs.SelectedTab = activeKind == ProfileKind.Longitudinal ? longPage : crossPage; }
                finally { loading = oldLoading; }
                SetStatus(error.Message, true);
                return false;
            }
        }
    }
}
