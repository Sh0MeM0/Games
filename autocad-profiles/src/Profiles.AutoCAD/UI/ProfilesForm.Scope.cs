using System;
using System.Drawing;
using System.Windows.Forms;
using Profiles.Core;

namespace Profiles.AutoCAD
{
    public sealed partial class ProfilesForm
    {
        private readonly ComboBox buildScope = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, Width = 185,
            Margin = new Padding(3, 4, 4, 3)
        };
        private Control scopeControl;
        private Button buildButton;
        private Button rebuildButton;
        private string displayedScope;
        private bool updatingScope;
        private ProfileKind? displayedScopeKind;
        private bool allCrossSections;
        private bool allLongitudinalProfiles;

        public string SelectedSectionId => selectedSection >= 0 && selectedSection < sections.Count
            ? sections[selectedSection].SectionId : null;

        public bool AllSections => allCrossSections;
        public bool AllProfiles => allLongitudinalProfiles;

        private Control BuildScopeControl()
        {
            buildScope.Items.AddRange(new object[] { "Текущий поперечник", "Все поперечники" });
            buildScope.SelectedIndex = 0;
            var panel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            panel.Controls.Add(Caption("Область"));
            panel.Controls.Add(buildScope);
            scopeControl = panel;
            tips.SetToolTip(buildScope, "Проверка, предпросмотр и построение используют одну область. По умолчанию строится только текущий профиль; остальные построения сохраняются.");
            buildScope.SelectedIndexChanged += (s, e) =>
            {
                if (updatingScope) return;
                if (activeKind == ProfileKind.CrossSection) allCrossSections = buildScope.SelectedIndex == 1;
                else allLongitudinalProfiles = buildScope.SelectedIndex == 1;
                UpdateModeCaption();
            };
            return panel;
        }

        private void UpdateScopeControls()
        {
            bool cross = activeKind == ProfileKind.CrossSection;
            updatingScope = true;
            try
            {
                if (displayedScopeKind != activeKind)
                {
                    buildScope.Items.Clear();
                    buildScope.Items.AddRange(cross
                        ? new object[] { "Текущий поперечник", "Все поперечники" }
                        : new object[] { "Текущий продольный", "Все продольные" });
                    displayedScopeKind = activeKind;
                }
                buildScope.SelectedIndex = (cross ? AllSections : AllProfiles) ? 1 : 0;
            }
            finally { updatingScope = false; }
            string name = selectedSection >= 0 && selectedSection < sections.Count
                ? SectionCaption(sections[selectedSection]) : "не выбран";
            modeCaption.Text = cross
                ? (AllSections ? "Построение: все поперечники" : "Построение: " + name)
                : AllProfiles ? "Построение: все продольные профили" : "Построение: текущий продольный профиль";
            if (scopeControl != null) scopeControl.Visible = true;
            if (buildButton != null)
            {
                bool all = cross ? AllSections : AllProfiles;
                buildButton.Text = all ? "Построить все" : "Построить текущий";
                rebuildButton.Text = all ? "Перестроить все" : "Перестроить текущий";
                tips.SetToolTip(buildButton, cross
                    ? (AllSections ? "Разместить все поперечники текущей вкладки профиля в новой точке. Каждое построение заменяется по своему идентификатору."
                        : "Разместить выбранный поперечник в новой точке. Остальные поперечники и продольный профиль не заменяются.")
                    : AllProfiles ? "Разместить все продольные профили проекта в новой точке; поперечники не заменяются."
                        : "Построить текущий продольный профиль в новой точке; остальные профили не заменяются.");
                tips.SetToolTip(rebuildButton, cross
                    ? (AllSections ? "Перестроить все поперечники, сохранив отдельные положения. Для ранее не построенных поперечников используйте построение в новой точке."
                        : "Перестроить только выбранный поперечник в его прежней точке вставки и ориентации.")
                    : AllProfiles ? "Перестроить все продольные профили, сохранив положение каждого."
                        : "Перестроить текущий продольный профиль в прежней точке вставки и ориентации.");
            }
            string scope = !cross ? "long:" + (AllProfiles ? "all" : CurrentProfileId)
                : "cross:" + CurrentProfileId + ":" + (AllSections ? "all" : SelectedSectionId);
            if (scope != displayedScope)
            {
                displayedScope = scope;
                if (Plan != null)
                {
                    Plan = null;
                    preview.MarkStale();
                    previewNotes.Text = "Область построения изменена. Нажмите «Обновить».";
                }
            }
        }

        private DrawingPlan BuildScopedPlan(ProfileProject project)
        {
            return ProfileBuildScope.Build(project, SelectedSectionId, AllSections, AllProfiles);
        }

        private void RestoreSectionSelection(string sectionId)
        {
            if (string.IsNullOrWhiteSpace(sectionId)) return;
            int index = sections.FindIndex(section => string.Equals(section.SectionId, sectionId, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return;
            bool old = loading;
            loading = true;
            try
            {
                selectedSection = index;
                RefreshSectionList(index);
                DisplaySection();
            }
            finally { loading = old; }
        }
    }
}
