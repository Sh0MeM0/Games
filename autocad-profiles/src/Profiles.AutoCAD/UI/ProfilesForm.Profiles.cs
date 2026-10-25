using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Profiles.Core;

namespace Profiles.AutoCAD
{
    public sealed partial class ProfilesForm
    {
        private readonly TabControl profileTabs = new TabControl
        {
            Dock = DockStyle.Fill, Height = Px(31), Multiline = false,
            Padding = new Point(12, 3), Margin = new Padding(0, 3, 4, 0)
        };
        private readonly Dictionary<string, string> profileSectionSelections = new Dictionary<string, string>();
        private ProfileProject profileContainer;
        private bool updatingProfileTabs;

        public string CurrentProfileId => profileContainer?.ProfileId;

        private ProfileProject CloneProfileContainer()
        {
            return profileContainer == null ? new ProfileProject() : ProjectFile.Clone(profileContainer);
        }

        private Control BuildProfilesBar()
        {
            var bar = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, RowCount = 1,
                Margin = new Padding(0), Padding = new Padding(0, 0, 0, 3)
            };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.Controls.Add(profileTabs, 0, 0);
            bar.Controls.Add(Button("+ Профиль", AddProfile), 1, 0);
            bar.Controls.Add(Button("Имя профиля…", RenameProfile), 2, 0);
            profileTabs.SelectedIndexChanged += ProfileTabChanged;
            tips.SetToolTip(profileTabs, "Профили одного проекта. У каждой вкладки свои продольные данные, поперечники, шаблон и масштабы.");
            return bar;
        }

        private void RefreshProfileTabs()
        {
            updatingProfileTabs = true;
            try
            {
                foreach (TabPage page in profileTabs.TabPages.Cast<TabPage>().ToArray())
                {
                    profileTabs.TabPages.Remove(page);
                    page.Dispose();
                }
                if (profileContainer == null) return;
                foreach (var profile in ProfileCollection.Enumerate(profileContainer))
                {
                    var page = new TabPage(profile.Name) { Tag = profile.ProfileId, ToolTipText = profile.Name };
                    profileTabs.TabPages.Add(page);
                    if (string.Equals(profile.ProfileId, CurrentProfileId, StringComparison.OrdinalIgnoreCase))
                        profileTabs.SelectedTab = page;
                }
                profileTabs.ShowToolTips = true;
            }
            finally { updatingProfileTabs = false; }
        }

        private string ProfileSelectionKey => currentProjectId + ":" + CurrentProfileId;

        private void RememberProfileSection()
        {
            if (SelectedSectionId != null) profileSectionSelections[ProfileSelectionKey] = SelectedSectionId;
        }

        private void RestoreProfileSection()
        {
            if (profileSectionSelections.TryGetValue(ProfileSelectionKey, out var sectionId))
                RestoreSectionSelection(sectionId);
        }

        private void ProfileTabChanged(object sender, EventArgs e)
        {
            if (loading || updatingProfileTabs || !(profileTabs.SelectedTab?.Tag is string requested) ||
                string.Equals(requested, CurrentProfileId, StringComparison.OrdinalIgnoreCase)) return;
            if (!TrySaveCurrent(true)) { RefreshProfileTabs(); return; }
            try
            {
                RememberProfileSection();
                var selected = ProfileCollection.Select(Project, requested);
                LoadProject(selected);
                RestoreProfileSection();
                SetDirty();
                if (TrySaveCurrent(true))
                    SetStatus("Открыт профиль «" + selected.ProfileName + "». Продольные данные, поперечники и масштабы этой вкладки независимы.");
            }
            catch (Exception error) { RefreshProfileTabs(); ShowError(error.Message); }
        }

        private void AddProfile()
        {
            if (!TrySaveCurrent(true)) return;
            var profiles = ProfileCollection.Enumerate(Project);
            int number = 1;
            while (profiles.Any(profile => string.Equals(profile.Name, "Профиль " + number, StringComparison.OrdinalIgnoreCase))) number++;
            string name = AskProjectName("Новый профиль", "Профиль " + number, "Название профиля", 200);
            if (name == null) return;
            try
            {
                RememberProfileSection();
                LoadProject(ProfileCollection.Add(Project, name));
                SetDirty();
                if (TrySaveCurrent(true)) SetStatus("Создан профиль «" + name + "». Введите его продольные данные или возьмите их из чертежа.");
            }
            catch (Exception error) { ShowError(error.Message); }
        }

        private void RenameProfile()
        {
            if (!TrySaveCurrent(true)) return;
            string name = AskProjectName("Переименовать профиль", Project.ProfileName, "Название профиля", 200);
            if (name == null || name == Project.ProfileName) return;
            try
            {
                RememberProfileSection();
                LoadProject(ProfileCollection.Rename(Project, CurrentProfileId, name));
                RestoreProfileSection();
                SetDirty();
                TrySaveCurrent(true);
            }
            catch (Exception error) { ShowError(error.Message); }
        }

        private void TemplateSelectionChanged(object sender, EventArgs e)
        {
            if (loading || profileContainer == null) return;
            ProfileTemplate requested = SelectedTemplate;
            ProfileTemplate previous = profileContainer.Template;
            if (requested == previous) return;
            // SelectionChangeCommitted runs after the combobox changed. Restore
            // the outgoing template before reading its editor, so its original
            // columns and identity are saved under the correct profile type.
            bool old = loading;
            loading = true;
            try { SelectTemplate(previous); }
            finally { loading = old; }
            if (!TrySaveCurrent(true)) return;
            try
            {
                RememberProfileSection();
                string prefix = requested == ProfileTemplate.NewChannel ? "Новый канал" :
                    requested == ProfileTemplate.Nvk ? "Сети НВК" : "Подчистка канала";
                var names = ProfileCollection.Enumerate(Project).Select(profile => profile.Name).ToList();
                string name = prefix;
                int number = 2;
                while (names.Any(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)))
                    name = prefix + " " + number++;
                var created = ProfileCollection.Add(Project, name);
                created.Template = requested;
                LoadProject(created);
                SetDirty();
                if (TrySaveCurrent(true))
                    SetStatus("Создана пустая вкладка «" + name + "». Исходный профиль со всеми данными сохранён на прежней вкладке.");
            }
            catch (Exception error) { ShowError(error.Message); }
        }
    }
}
