using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using Profiles.Core;

namespace Profiles.AutoCAD
{
    /// <summary>
    /// Collectors flowing into each other. A click on «Впадает коллектор» lists the
    /// project's drainage tabs; a chosen one flows in at that row's picket and is
    /// rebuilt from this collector on every save (outlet surface and drain elevation,
    /// slopes kept). «Новый коллектор, впадающий здесь…» creates such a tab.
    /// </summary>
    public sealed partial class ProfilesForm
    {
        private ContextMenuStrip inflowMenu;
        private Font inflowHeaderFont;
        private string lastNetworkWarnings;
        private ProfileProject outletSource;
        private OutletState outletState;

        /// <summary>The outlet of the open tributary, computed once per saved project state.</summary>
        private sealed class OutletState
        {
            internal double Station;
            internal bool Ground, Bottom;
            internal DrainageNetwork.Link Receiver;
        }
        private const double StationTolerance = 1e-6;

        /// <summary>Test hook: answers «move the junction here?» without a message box.</summary>
        internal Func<string, bool> ConfirmInflowMove { get; set; }
        /// <summary>Test hook: the name of a new tributary instead of the input dialog (null cancels).</summary>
        internal Func<string, string> AskTributaryName { get; set; }

        private bool DrainageGrid => SelectedTemplate == ProfileTemplate.Drainage && activeKind == ProfileKind.Longitudinal;

        private void InitializeDrainageNetwork()
        {
            inflowMenu = new ContextMenuStrip { ShowCheckMargin = true, ShowImageMargin = false };
            ownedComponents.Add(inflowMenu);
            inflowHeaderFont = new Font(inflowMenu.Font, FontStyle.Bold);
            longGrid.CellMouseClick += (s, e) =>
            {
                if (e.Button != MouseButtons.Left || e.RowIndex < 0 || e.ColumnIndex < 0 || !DrainageGrid) return;
                if (longGrid.Columns[e.ColumnIndex].Name != "PointName" || longGrid.IsCurrentCellInEditMode) return;
                ShowInflowMenu(e.RowIndex);
            };
            longGrid.KeyDown += (s, e) =>
            {
                if (!DrainageGrid || longGrid.CurrentCell == null || longGrid.IsCurrentCellInEditMode ||
                    longGrid.Columns[longGrid.CurrentCell.ColumnIndex].Name != "PointName") return;
                if ((e.KeyCode == Keys.F4 && e.Modifiers == Keys.None) || (e.KeyCode == Keys.Down && e.Alt))
                {
                    e.Handled = e.SuppressKeyPress = true;
                    ShowInflowMenu(longGrid.CurrentCell.RowIndex);
                }
            };
            longGrid.CellPainting += PaintInflowCell;
            longGrid.CellBeginEdit += (s, e) =>
            {
                string reason = OutletLock(e.RowIndex, e.ColumnIndex);
                if (reason == null) return;
                e.Cancel = true;
                SetStatus(reason);
            };
            longGrid.CellFormatting += (s, e) =>
            {
                if (OutletLock(e.RowIndex, e.ColumnIndex) != null) e.CellStyle.BackColor = Color.FromArgb(236, 236, 236);
            };
            longGrid.CellToolTipTextNeeded += (s, e) =>
            {
                string reason = OutletLock(e.RowIndex, e.ColumnIndex);
                if (reason != null) { e.ToolTipText = reason; return; }
                if (DrainageGrid && e.RowIndex >= 0 && e.ColumnIndex >= 0 && longGrid.Columns[e.ColumnIndex].Name == "PointName")
                    e.ToolTipText = "Щелчок или F4 — список дренажных коллекторов проекта: отметьте тот, который впадает на этом пикете, " +
                        "или создайте новый. Впадающий коллектор начинается с отметок этого коллектора и перестраивается при их изменении.";
            };
        }

        private void PaintInflowCell(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (!DrainageGrid || e.RowIndex < 0 || e.ColumnIndex < 0 || longGrid.Columns[e.ColumnIndex].Name != "PointName") return;
            e.Paint(e.CellBounds, DataGridViewPaintParts.All);
            // A drop-down mark: the cell opens a list.
            int size = Math.Max(4, e.CellBounds.Height / 5);
            int x = e.CellBounds.Right - size * 2 - 3, y = e.CellBounds.Top + (e.CellBounds.Height - size) / 2;
            using (var brush = new SolidBrush(Color.FromArgb(110, 110, 110)))
                e.Graphics.FillPolygon(brush, new[] { new Point(x, y), new Point(x + size * 2, y), new Point(x + size, y + size) });
            e.Handled = true;
        }

        private OutletState CurrentOutlet()
        {
            var project = Project;
            if (ReferenceEquals(project, outletSource)) return outletState;
            outletSource = project;
            outletState = null;
            if (project == null || project.Template != ProfileTemplate.Drainage || project.Longitudinal == null || project.Longitudinal.Count == 0) return null;
            var receiver = DrainageNetwork.Receiver(project);
            if (receiver == null) return null;
            var main = (project.AdditionalProfiles ?? new List<ProfileData>()).FirstOrDefault(profile => SameId(profile.ProfileId, receiver.MainProfileId));
            if (main?.Longitudinal == null) return null;
            LongitudinalPoint junction;
            try { junction = ProfileInterpolation.SampleLongitudinal(main.Longitudinal, receiver.Station); }
            catch (ProfileValidationException) { return null; }
            return outletState = new OutletState
            {
                Station = project.Longitudinal.Min(point => point.Station), Receiver = receiver,
                Ground = junction.Ground.HasValue, Bottom = junction.ProjectBottom.HasValue
            };
        }

        /// <summary>The reason an outlet cell of a tributary is not edited here, or null.</summary>
        private string OutletLock(int rowIndex, int columnIndex)
        {
            if (!DrainageGrid || rowIndex < 0 || columnIndex < 0 || rowIndex >= longGrid.Rows.Count) return null;
            string column = longGrid.Columns[columnIndex].Name;
            if (column != "ProjectBottom" && column != "Ground") return null;
            var outlet = CurrentOutlet();
            if (outlet == null || (column == "ProjectBottom" ? !outlet.Bottom : !outlet.Ground)) return null;
            var row = longGrid.Rows[rowIndex];
            if (row.IsNewRow) return null;
            var raw = row.Cells["Station"].Value;
            double station;
            if (raw is double number) station = number;
            else if (!TryStation(row, out station)) return null;
            if (Math.Abs(station - outlet.Station) > StationTolerance) return null;
            var receiver = outlet.Receiver;
            return "Устье «" + receiver.TributaryName + "» — точка коллектора «" + receiver.MainName + "» на ПК " + DrainageNetwork.Picket(receiver.Station) +
                ": " + (column == "ProjectBottom" ? "отметка дрены" : "отметка поверхности") + " берётся с него. Измените её на вкладке «" +
                receiver.MainName + "» — этот коллектор перестроится, уклоны сохранятся.";
        }

        private static bool TryStation(DataGridViewRow row, out double station)
        {
            station = 0;
            try { station = Required(row, "Station"); return true; }
            catch (ProfileValidationException) { return false; }
        }

        private void ShowInflowMenu(int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= longGrid.Rows.Count) return;
            var row = longGrid.Rows[rowIndex];
            longGrid.CurrentCell = row.Cells["PointName"];
            BuildInflowMenu(row);
            var bounds = longGrid.GetCellDisplayRectangle(longGrid.Columns["PointName"].Index, rowIndex, false);
            inflowMenu.Show(longGrid, new Point(bounds.Left, bounds.Bottom));
        }

        /// <summary>Fills the list for one row: drainage tabs (checked when they flow in here), a new tributary, manual text.</summary>
        private void BuildInflowMenu(DataGridViewRow row)
        {
            foreach (ToolStripItem item in inflowMenu.Items.Cast<ToolStripItem>().ToArray()) { inflowMenu.Items.Remove(item); item.Dispose(); }
            if (row.IsNewRow || !TryStation(row, out double station))
            {
                inflowMenu.Items.Add(new ToolStripMenuItem("Сначала введите ПК этой строки") { Enabled = false });
                inflowMenu.Items.Add(new ToolStripMenuItem("Ввести подпись вручную", null, (s, e) => EditInflowCell(row)));
                return;
            }
            // The list reflects the saved project; unsaved input is saved first.
            if (dirty) TrySaveCurrent(false);
            var project = Project;
            var members = DrainageNetwork.Members(project);
            var current = members.FirstOrDefault(member => SameId(member.ProfileId, CurrentProfileId));
            var tokens = DrainageNetwork.Tokens(StringCell(row, "PointName"));
            var downstream = Downstream(project);
            inflowMenu.Items.Add(new ToolStripMenuItem("Впадает в «" + (current?.Name ?? title.Text.Trim()) + "» на ПК " + DrainageNetwork.Picket(station) + ":")
            {
                Enabled = false, Font = inflowHeaderFont
            });
            var others = members.Where(member => !SameId(member.ProfileId, CurrentProfileId)).ToList();
            if (others.Count == 0)
                inflowMenu.Items.Add(new ToolStripMenuItem("В проекте нет других дренажных коллекторов") { Enabled = false });
            foreach (var member in others)
            {
                var receiver = member.Receiver;
                bool here = tokens.Any(member.Matches);
                bool elsewhere = receiver != null && !(SameId(receiver.MainProfileId, CurrentProfileId) && Math.Abs(receiver.Station - station) < StationTolerance);
                string note = downstream.Contains(member.ProfileId) ? "  — принимает этот коллектор"
                    : elsewhere ? "  — сейчас впадает в «" + receiver.MainName + "» на ПК " + DrainageNetwork.Picket(receiver.Station) : "";
                var item = new ToolStripMenuItem(member.Name + note)
                {
                    Checked = here, CheckOnClick = false, Enabled = here || !downstream.Contains(member.ProfileId), Tag = member.ProfileId,
                    ToolTipText = here ? "Убрать впадение «" + member.Name + "» на этом пикете. Отметки коллектора не изменятся."
                        : "«" + member.Name + "» впадает здесь: его устье получит отметки этого коллектора на ПК " + DrainageNetwork.Picket(station) + "."
                };
                var chosen = member;
                item.Click += (s, e) => ToggleInflow(row, chosen);
                inflowMenu.Items.Add(item);
            }
            inflowMenu.Items.Add(new ToolStripSeparator());
            inflowMenu.Items.Add(new ToolStripMenuItem("Новый коллектор, впадающий здесь…", null, (s, e) => CreateTributary(row))
            {
                ToolTipText = "Создать вкладку дренажного коллектора, впадающего на ПК " + DrainageNetwork.Picket(station) +
                    ": устье — с отметками этого коллектора, масштабы — как у него."
            });
            inflowMenu.Items.Add(new ToolStripMenuItem("Ввести подпись вручную", null, (s, e) => EditInflowCell(row))
            {
                ToolTipText = "Свободная подпись выноски, например номер существующего коллектора. Несколько — через запятую."
            });
            inflowMenu.Items.Add(new ToolStripMenuItem("Очистить", null, (s, e) => WriteInflow(row, new List<string>(), "Впадение на ПК " + DrainageNetwork.Picket(station) + " убрано."))
            {
                Enabled = tokens.Count > 0
            });
        }

        private static bool SameId(string first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

        /// <summary>The collectors the active profile drains into, directly or further down: none of them can flow into it.</summary>
        private HashSet<string> Downstream(ProfileProject project)
        {
            var links = DrainageNetwork.Links(project);
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string currentId = CurrentProfileId;
            while (currentId != null)
            {
                string id = currentId;
                var link = links.FirstOrDefault(item => SameId(item.TributaryProfileId, id));
                if (link == null || !result.Add(link.MainProfileId)) break;
                currentId = link.MainProfileId;
            }
            return result;
        }

        private void EditInflowCell(DataGridViewRow row)
        {
            if (row.DataGridView != longGrid) return;
            longGrid.CurrentCell = row.Cells["PointName"];
            longGrid.BeginEdit(true);
        }

        private void ToggleInflow(DataGridViewRow row, DrainageNetwork.Member member)
        {
            if (row.DataGridView != longGrid || !TryStation(row, out double station)) return;
            var tokens = DrainageNetwork.Tokens(StringCell(row, "PointName"));
            if (tokens.Any(member.Matches))
            {
                tokens.RemoveAll(member.Matches);
                WriteInflow(row, tokens, "«" + member.Name + "» больше не впадает на ПК " + DrainageNetwork.Picket(station) + ". Его отметки не изменены.");
                return;
            }
            var receiver = member.Receiver;
            if (receiver != null && !(SameId(receiver.MainProfileId, CurrentProfileId) && Math.Abs(receiver.Station - station) < StationTolerance))
            {
                string question = "«" + member.Name + "» уже впадает в «" + receiver.MainName + "» на ПК " + DrainageNetwork.Picket(receiver.Station) +
                    ".\n\nПеренести впадение сюда — на ПК " + DrainageNetwork.Picket(station) + "? Коллектор перестроится по новому месту впадения, уклоны сохранятся.";
                bool move = ConfirmInflowMove != null ? ConfirmInflowMove(question)
                    : MessageBox.Show(this, question, "Впадение коллектора", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
                if (!move) return;
                RemoveInflow(receiver, member);
            }
            tokens.Add(member.Label);
            WriteInflow(row, tokens, null);
        }

        /// <summary>Removes the labels naming a tributary from its old junction, on this tab or another.</summary>
        private void RemoveInflow(DrainageNetwork.Link link, DrainageNetwork.Member member)
        {
            if (SameId(link.MainProfileId, CurrentProfileId))
            {
                foreach (DataGridViewRow other in longGrid.Rows)
                {
                    if (other.IsNewRow || !TryStation(other, out double value) || Math.Abs(value - link.Station) > StationTolerance) continue;
                    var tokens = DrainageNetwork.Tokens(StringCell(other, "PointName"));
                    tokens.RemoveAll(member.Matches);
                    other.Cells["PointName"].Value = string.Join(", ", tokens);
                }
                return;
            }
            var container = CloneProfileContainer();
            var profile = (container.AdditionalProfiles ?? new List<ProfileData>()).FirstOrDefault(item => SameId(item.ProfileId, link.MainProfileId));
            if (profile == null) return;
            foreach (var point in profile.Longitudinal.Where(point => Math.Abs(point.Station - link.Station) < StationTolerance))
            {
                var tokens = DrainageNetwork.Tokens(point.PointName);
                tokens.RemoveAll(member.Matches);
                point.PointName = string.Join(", ", tokens);
            }
            profileContainer = container;
        }

        private void WriteInflow(DataGridViewRow row, List<string> tokens, string message)
        {
            if (row.DataGridView != longGrid) return;
            longGrid.EndEdit();
            row.Cells["PointName"].Value = string.Join(", ", tokens);
            SetDirty();
            if (TrySaveCurrent(false) && message != null) SetStatus(message);
            longGrid.InvalidateColumn(longGrid.Columns["PointName"].Index);
        }

        private void CreateTributary(DataGridViewRow row)
        {
            if (row.DataGridView != longGrid || !TryStation(row, out double station)) return;
            if (!TrySaveCurrent(true)) return;
            try
            {
                var saved = Project;
                string mainId = CurrentProfileId;
                var members = DrainageNetwork.Members(saved);
                string mainName = members.FirstOrDefault(member => SameId(member.ProfileId, mainId))?.Name ?? saved.ProfileName;
                string suggested = DrainageNetwork.TributaryName(saved, mainId);
                string name = AskTributaryName != null ? AskTributaryName(suggested)
                    : AskProjectName("Новый впадающий коллектор", suggested, "Обозначение коллектора (первая строка заголовка)", 200);
                if (name == null) return;
                name = name.Trim();
                // The label written in the collector's row: the number, unless another tab already uses it.
                string number = DrainageNetwork.Number(name);
                string label = members.Any(member => string.Equals(member.Number, number, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(member.Name, number, StringComparison.OrdinalIgnoreCase)) ? name : number;
                var withLabel = ProjectFile.Clone(saved);
                var junction = withLabel.Longitudinal.FirstOrDefault(point => Math.Abs(point.Station - station) < StationTolerance);
                if (junction == null) throw new ProfileValidationException("Строка ПК " + DrainageNetwork.Picket(station) + " не сохранена. Проверьте данные таблицы.");
                var tokens = DrainageNetwork.Tokens(junction.PointName);
                tokens.Add(label);
                junction.PointName = string.Join(", ", tokens);
                // A new tab takes this collector's type, scales and text settings.
                var created = ProfileCollection.Add(withLabel, name);
                created.Settings.Title = name;
                created.Settings.Subtitle = "";
                created.Settings.OutletElevation = null;
                var changes = new List<DrainageNetwork.Change>();
                created = DrainageNetwork.Synchronize(created, changes);
                RememberProfileSection();
                LoadProject(created);
                SetDirty();
                if (!TrySaveCurrent(true)) return;
                var outlet = created.Longitudinal.OrderBy(point => point.Station).FirstOrDefault();
                SetStatus("Создан «" + name + "»: впадает в «" + mainName + "» на ПК " + DrainageNetwork.Picket(station) + ". " +
                    (outlet?.ProjectBottom.HasValue == true || outlet?.Ground.HasValue == true
                        ? "Устье взято с «" + mainName + "»" + (outlet.Ground.HasValue ? ": поверхность " + outlet.Ground.Value.ToString("0.00", CultureInfo.InvariantCulture) : ":") +
                          (outlet.ProjectBottom.HasValue ? (outlet.Ground.HasValue ? "," : "") + " дрена " + outlet.ProjectBottom.Value.ToString("0.00", CultureInfo.InvariantCulture) : "") + ". "
                        : "Отметки устья появятся, когда у «" + mainName + "» будут отметки на этом пикете. ") +
                    "Введите ординаты коллектора.");
            }
            catch (Exception error) { ShowError(error.Message); }
        }

        /// <summary>
        /// The project with every tributary rebuilt from its collector. When the open
        /// tab is a tributary that changed, its table and receiver line are updated.
        /// The message says what was rebuilt; null when nothing changed.
        /// </summary>
        private ProfileProject SynchronizeNetwork(ProfileProject project, out string message, out bool warning)
        {
            message = null;
            warning = false;
            if (project == null) return null;
            var warnings = new List<string>();
            if (DrainageNetwork.Links(project, warnings).Count == 0 && warnings.Count == 0) { lastNetworkWarnings = null; return project; }
            warnings.Clear();
            var changes = new List<DrainageNetwork.Change>();
            ProfileProject synced;
            try { synced = DrainageNetwork.Synchronize(project, changes, warnings); }
            catch (ProfileValidationException error)
            {
                message = "Связанные коллекторы не перестроены: " + error.Message;
                warning = true;
                return project;
            }
            string warningText = warnings.Count == 0 ? null : string.Join(" ", warnings);
            if (changes.Count > 0)
            {
                message = changes[0].Text + (changes.Count > 1 ? " Перестроено коллекторов: " + changes.Count + "." : "");
                if (changes.Any(change => SameId(change.ProfileId, synced.ProfileId))) ShowSynchronizedProfile(synced);
            }
            if (warningText != null && warningText != lastNetworkWarnings)
            {
                message = message == null ? warningText : message + " " + warningText;
                warning = true;
            }
            lastNetworkWarnings = warningText;
            return synced;
        }

        /// <summary>Shows the rebuilt outlet of the open tributary without reloading the editor.</summary>
        private void ShowSynchronizedProfile(ProfileProject synced)
        {
            bool old = loading;
            loading = true;
            try
            {
                ApplyLongitudinalValues(synced.Longitudinal);
                sharedLongitudinalBaseline = CopyLongitudinal(synced.Longitudinal);
                if (subtitle.Text.Trim() != (synced.Settings.Subtitle ?? "")) subtitle.Text = synced.Settings.Subtitle ?? "";
            }
            finally { loading = old; }
            RecalculateDerivedValues();
            longGrid.Invalidate();
        }
    }
}
