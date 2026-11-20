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
        private readonly Dictionary<string, string> lastProfileByTemplate = new Dictionary<string, string>();
        private Button deleteProfileButton;
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
                Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4, RowCount = 1,
                Margin = new Padding(0), Padding = new Padding(0, 0, 0, 3)
            };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.Controls.Add(profileTabs, 0, 0);
            bar.Controls.Add(Button("+ Профиль", AddProfile), 1, 0);
            bar.Controls.Add(Button("Имя профиля…", RenameProfile), 2, 0);
            deleteProfileButton = Button("Удалить профиль…", DeleteProfile);
            bar.Controls.Add(deleteProfileButton, 3, 0);
            tips.SetToolTip(deleteProfileButton, "Удалить выбранный продольный профиль и его поперечники. Другие вкладки сохраняются.");
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
                foreach (var profile in ProfileCollection.Describe(profileContainer))
                {
                    var page = new TabPage(TabCaption(profile.Name, profile.Template))
                    {
                        Tag = profile.ProfileId,
                        ToolTipText = profile.Name + " — " + TemplateName(profile.Template) + (profile.HasData ? "" : " (пока без данных)")
                    };
                    profileTabs.TabPages.Add(page);
                    if (string.Equals(profile.ProfileId, CurrentProfileId, StringComparison.OrdinalIgnoreCase))
                        profileTabs.SelectedTab = page;
                }
                profileTabs.ShowToolTips = true;
                if (deleteProfileButton != null) deleteProfileButton.Enabled = profileTabs.TabCount > 1;
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
            var profiles = ProfileCollection.Describe(Project);
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

        private void DeleteProfile()
        {
            if (profileContainer == null || ProfileCollection.Describe(profileContainer).Count <= 1) return;
            if (!TrySaveCurrent(true)) return;
            if (MessageBox.Show(this,
                "Удалить профиль «" + Project.ProfileName + "»?\n\n" +
                "Будут удалены его продольные данные и поперечники. Остальные профили проекта сохранятся. " +
                "Уже построенные объекты останутся в чертеже; при необходимости удалите их вручную.\n\n" +
                (autosave != null
                    ? "Состояние до удаления останется в «Версии…»."
                    : "Чтобы сохранить копию, нажмите «Нет» и экспортируйте проект в JSON."),
                "Удаление профиля", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            try { RemoveCurrentProfile(); }
            catch (Exception error) { ShowError(error.Message); }
        }

        private void RemoveCurrentProfile()
        {
            // The removed tab stays available in «Версии…».
            try { if (Library != null && currentProjectId != null) Library.Checkpoint(currentProjectId); }
            catch (ProfileValidationException error) { SetStatus(error.Message, true); }
            var remaining = ProfileCollection.Remove(Project, CurrentProfileId);
            string removedSelection = ProfileSelectionKey;
            autoSaveTimer.Stop();
            interpolationTimer.Stop();
            LoadProject(remaining);
            profileSectionSelections.Remove(removedSelection);
            RestoreProfileSection();
            SetDirty();
            if (TrySaveCurrent(true))
                SetStatus("Профиль удалён. Открыт профиль «" + remaining.ProfileName + "». Построенные объекты остались в чертеже.");
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

        internal enum TemplateSwitchChoice { Cancel, OpenExisting, ChangeCurrent, CreateNew }

        /// <summary>Chooses what a type change does for a profile with data; replaced in UI tests.</summary>
        internal Func<string, string, string, TemplateSwitchChoice> ChooseTemplateSwitch { get; set; }

        internal static string TemplateName(ProfileTemplate template) =>
            template == ProfileTemplate.NewChannel ? "Новый канал" : template == ProfileTemplate.Nvk ? "Сети НВК" : "Подчистка канала";

        private static string TemplateShortName(ProfileTemplate template) =>
            template == ProfileTemplate.NewChannel ? "новый канал" : template == ProfileTemplate.Nvk ? "НВК" : "подчистка";

        /// <summary>The type is shown on every tab, so it is always visible where the data of each type lives.</summary>
        internal static string TabCaption(string name, ProfileTemplate template)
        {
            string marker = template == ProfileTemplate.NewChannel ? "новый канал" : template == ProfileTemplate.Nvk ? "нвк" : "подчист";
            return (name ?? "").IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0 ? name : name + " · " + TemplateShortName(template);
        }

        private string TemplateMemoryKey(ProfileTemplate template) => currentProjectId + ":" + template;

        private void RememberProfileTemplate()
        {
            if (profileContainer != null && currentProjectId != null)
                lastProfileByTemplate[TemplateMemoryKey(profileContainer.Template)] = profileContainer.ProfileId;
        }

        private ProfileSummary ProfileForTemplate(List<ProfileSummary> profiles, ProfileTemplate template)
        {
            var candidates = profiles.Where(profile => profile.Template == template &&
                !string.Equals(profile.ProfileId, CurrentProfileId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (lastProfileByTemplate.TryGetValue(TemplateMemoryKey(template), out var remembered))
            {
                var match = candidates.FirstOrDefault(profile => string.Equals(profile.ProfileId, remembered, StringComparison.OrdinalIgnoreCase));
                if (match != null) return match;
            }
            // Prefer a tab that already holds work over an empty one.
            return candidates.LastOrDefault(profile => profile.HasData) ?? candidates.LastOrDefault();
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
                var profiles = ProfileCollection.Describe(Project);
                var current = profiles.First(profile => string.Equals(profile.ProfileId, CurrentProfileId, StringComparison.OrdinalIgnoreCase));
                var existing = ProfileForTemplate(profiles, requested);
                TemplateSwitchChoice choice;
                if (!current.HasData) choice = TemplateSwitchChoice.ChangeCurrent;
                else
                {
                    var chooser = ChooseTemplateSwitch ?? AskTemplateSwitch;
                    choice = chooser(TemplateName(requested), current.Name, existing?.Name);
                    if (choice == TemplateSwitchChoice.OpenExisting && existing == null) choice = TemplateSwitchChoice.CreateNew;
                }
                switch (choice)
                {
                    case TemplateSwitchChoice.OpenExisting:
                        RememberProfileSection();
                        LoadProject(ProfileCollection.Select(Project, existing.ProfileId));
                        RestoreProfileSection();
                        SetDirty();
                        if (TrySaveCurrent(true))
                            SetStatus("Открыта вкладка «" + existing.Name + "» (" + TemplateName(requested) + "). Данные вкладки «" + current.Name + "» сохранены.");
                        break;
                    case TemplateSwitchChoice.ChangeCurrent:
                        RememberProfileSection();
                        LoadProject(ProfileCollection.ChangeTemplate(Project, CurrentProfileId, requested));
                        RestoreProfileSection();
                        SetDirty();
                        if (TrySaveCurrent(true))
                            SetStatus(current.HasData
                                ? "Тип профиля «" + current.Name + "» изменён на «" + TemplateName(requested) + "». Все введённые данные сохранены; столбцы прежнего типа скрыты, их значения хранятся и вернутся при обратной смене."
                                : "Тип профиля «" + current.Name + "»: " + TemplateName(requested) + ".");
                        break;
                    case TemplateSwitchChoice.CreateNew:
                        RememberProfileSection();
                        string prefix = TemplateName(requested);
                        string name = prefix;
                        int number = 2;
                        while (profiles.Any(profile => string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase)))
                            name = prefix + " " + number++;
                        var created = ProfileCollection.Add(Project, name);
                        created.Template = requested;
                        LoadProject(created);
                        SetDirty();
                        if (TrySaveCurrent(true))
                            SetStatus("Создана пустая вкладка «" + name + "». Профиль «" + current.Name + "» со всеми данными сохранён на своей вкладке.");
                        break;
                    default:
                        SetStatus("Тип профиля не изменён.");
                        break;
                }
            }
            catch (Exception error) { ShowError(error.Message); }
        }

        private TemplateSwitchChoice AskTemplateSwitch(string requested, string current, string existing)
        {
            using (var dialog = new Form
            {
                Text = "Тип профиля: " + requested, Font = Font, StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false,
                ShowInTaskbar = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink
            })
            {
                var choice = TemplateSwitchChoice.Cancel;
                var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(Px(12)) };
                layout.Controls.Add(new Label
                {
                    AutoSize = true, MaximumSize = new Size(Px(520), 0), Margin = new Padding(0, 0, 0, Px(8)),
                    Text = "В профиле «" + current + "» есть данные. Они сохранены и никуда не пропадут.\n\nЧто сделать с типом «" + requested + "»?"
                });
                Button Choice(string text, string hint, TemplateSwitchChoice value)
                {
                    var button = new Button { Text = text, AutoSize = true, MinimumSize = new Size(Px(520), Px(30)), TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(0, Px(2), 0, Px(2)) };
                    button.Click += (s, e) => { choice = value; dialog.DialogResult = DialogResult.OK; };
                    tips.SetToolTip(button, hint);
                    layout.Controls.Add(button);
                    return button;
                }
                Button first = null;
                if (existing != null)
                    first = Choice("Перейти к вкладке «" + existing + "» (" + requested + ")",
                        "Открыть уже созданный профиль этого типа со всеми его данными.", TemplateSwitchChoice.OpenExisting);
                var change = Choice("Сменить тип профиля «" + current + "» на «" + requested + "» (данные сохранятся)",
                    "Пикеты, отметки и поперечники остаются. Столбцы, которых нет у нового типа, скрываются; их значения хранятся и вернутся при обратной смене.", TemplateSwitchChoice.ChangeCurrent);
                var create = Choice("Создать новую пустую вкладку «" + requested + "»",
                    "Отдельный профиль этого типа. Профиль «" + current + "» остаётся на своей вкладке.", TemplateSwitchChoice.CreateNew);
                var cancel = new Button { Text = "Отмена", AutoSize = true, MinimumSize = new Size(Px(90), Px(28)), DialogResult = DialogResult.Cancel, Anchor = AnchorStyles.Right, Margin = new Padding(0, Px(8), 0, 0) };
                layout.Controls.Add(cancel);
                dialog.Controls.Add(layout);
                dialog.CancelButton = cancel;
                var accept = first ?? create;
                dialog.AcceptButton = accept;
                dialog.Shown += (s, e) => accept.Focus();
                return dialog.ShowDialog(this) == DialogResult.OK ? choice : TemplateSwitchChoice.Cancel;
            }
        }
    }
}
