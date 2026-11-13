using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Kzm.AutoCAD.Services;
using Kzm.Core;
using Font = System.Drawing.Font;

namespace Kzm.AutoCAD.UI.Panel
{
    /// <summary>«Порядок работы»: шаги с отметкой, что уже сделано в чертеже, и кнопками следующего действия.</summary>
    internal sealed class StartView : UserControl, IPanelView
    {
        private readonly FlowLayoutPanel root = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = System.Windows.Forms.FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(6),
        };
        private KzmPanel panel;

        internal StartView()
        {
            Controls.Add(root);
        }

        public void Bind(KzmPanel owner, string id)
        {
            panel = owner;
            root.SuspendLayout();
            try
            {
                root.Controls.Clear();
                root.Controls.Add(new Label { Text = "Порядок работы", AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 10, FontStyle.Bold) });
                var snapshot = panel.Snapshot;
                if (snapshot.Project != null)
                {
                    root.Controls.Add(Note("Проект «" + snapshot.Project.Name + "». Другой проект — кнопка «Проект» вверху панели.", SystemColors.ControlText));
                }
                if (panel.Document == null)
                {
                    root.Controls.Add(Note("Откройте чертёж.", Color.Firebrick));
                    return;
                }
                var facts = snapshot.Surfaces.Where(s => s.Info.Kind == SurfaceKind.Fact).ToList();
                var projects = snapshot.Surfaces.Where(s => s.Info.Kind == SurfaceKind.Project).ToList();
                var factWithPoints = facts.FirstOrDefault(s => s.Points >= 3);
                var projectWithPoints = projects.FirstOrDefault(s => s.Points >= 3);

                Step(1, "Фактическая поверхность (съёмка)",
                    facts.Count == 0 ? State.Todo("поверхности нет")
                    : factWithPoints == null ? State.Warn("«" + facts[0].Info.Name + "»: точек " + facts[0].Points + " — импортируйте отметки съёмки")
                    : State.Done("«" + factWithPoints.Info.Name + "» — " + factWithPoints.Points + " т."),
                    facts.Count == 0 ? Button("Создать…", () => panel.ShowNewSurface(SurfaceKind.Fact)) : null,
                    facts.Count > 0 ? Button("Импорт из текстов…", () => panel.ShowImport(facts[0].Info.Id)) : null,
                    factWithPoints != null ? Button("Свойства", () => panel.ShowSurface(factWithPoints.Info.Id)) : null);

                Step(2, "Проектная поверхность",
                    projects.Count == 0 ? State.Todo("поверхности нет")
                    : projectWithPoints == null ? State.Warn("«" + projects[0].Info.Name + "»: точек " + projects[0].Points + " — задайте проектные отметки")
                    : State.Done("«" + projectWithPoints.Info.Name + "» — " + projectWithPoints.Points + " т." +
                                 (projectWithPoints.StructureLines > 0 ? ", структурных линий " + projectWithPoints.StructureLines : string.Empty)),
                    projects.Count == 0 ? Button("Создать…", () => panel.ShowNewSurface(SurfaceKind.Project)) : null,
                    projects.Count > 0 ? Button("Импорт…", () => panel.ShowImport(projects[0].Info.Id)) : null,
                    projects.Count > 0 ? Button("Плоскость…", () => panel.ShowPlane(projects[0].Info.Id)) : null,
                    projects.Count > 0 ? Button("Свойства", () => panel.ShowSurface(projects[0].Info.Id)) : null);
                root.Controls.Add(Note("Проектные отметки: импорт текстов, ввод вручную, плоскость по 3 точкам или по уклонам, структурные линии по полилиниям " +
                                       "(бровки, лотки, бортовой камень).", SystemColors.GrayText));

                var cartograms = snapshot.Cartograms;
                var outdated = cartograms.Where(c => c.Outdated).ToList();
                var built = cartograms.Where(c => c.Info.Built && !c.Outdated).ToList();
                Step(3, "Картограмма: граница работ, поверхности, сетка",
                    cartograms.Count == 0 ? State.Todo("картограммы нет")
                    : outdated.Count > 0 ? State.Warn("устарела: «" + outdated[0].Info.Name + "» — точки или параметры изменились")
                    : built.Count > 0 ? State.Done("«" + built[0].Info.Name + "»: насыпь " + CartogramService.Volume(built[0].Info.LastFill, built[0].Info.VolumeDecimals, true) +
                                               ", выемка " + CartogramService.Volume(built[0].Info.LastCut, built[0].Info.VolumeDecimals, false) + " м³")
                    : State.Warn("«" + cartograms[0].Info.Name + "» не построена"),
                    cartograms.Count == 0 ? Button("Создать…", panel.ShowNewCartogram) : Button("Открыть", () => panel.ShowCartogram(cartograms[0].Info.Id)),
                    outdated.Count > 0 ? Button("Пересчитать устаревшие", panel.UpdateOutdated) : null);

                Step(4, "Ведомость объёмов и выгрузка",
                    built.Count > 0 ? State.Info("в свойствах картограммы: «Ведомость объёмов…» (рис. 9 СТБ 2073-2010) и «В Excel…»")
                    : State.Info("после построения картограммы"),
                    built.Count > 0 ? Button("Открыть картограмму", () => panel.ShowCartogram(built[0].Info.Id)) : null);

                root.Controls.Add(new Label { Text = "Подсказки", AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold), Margin = new Padding(3, 14, 3, 3) });
                root.Controls.Add(Note(
                    "• Правый щелчок по поверхности или картограмме в списке — все действия; двойной щелчок — показать на чертеже; Delete — удалить.\n" +
                    "• Отметки на картограмме — блоки: проектную и фактическую можно поправить в «Быстрых свойствах», «Пересчитать» запишет их в поверхности.\n" +
                    "• Точки — обычные блоки: двигайте, копируйте, удаляйте как всегда; отметку меняйте в «Свойствах» (атрибут ОТМЕТКА) или в таблице точек.\n" +
                    "• Каждое действие панели — одна отмена (U).\n" +
                    "• Проблемные места помечаются красным на слое КЗМ_ПРОВЕРКА (не печатается), убрать — «Убрать пометки».",
                    SystemColors.ControlText));
            }
            finally
            {
                root.ResumeLayout();
            }
        }

        private sealed class State
        {
            internal string Text;
            internal Color Color;
            internal string Mark;
            internal static State Done(string text) => new State { Text = text, Color = Color.ForestGreen, Mark = "✓ " };
            internal static State Todo(string text) => new State { Text = text, Color = Color.Firebrick, Mark = "○ " };
            internal static State Warn(string text) => new State { Text = text, Color = Color.DarkOrange, Mark = "! " };
            internal static State Info(string text) => new State { Text = text, Color = SystemColors.GrayText, Mark = string.Empty };
        }

        private void Step(int number, string title, State state, params Button[] buttons)
        {
            root.Controls.Add(new Label { Text = number + ". " + title, AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold), Margin = new Padding(3, 10, 3, 0) });
            root.Controls.Add(Note(state.Mark + state.Text, state.Color));
            var flow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(350, 0), Margin = new Padding(12, 0, 0, 0) };
            foreach (var button in buttons.Where(b => b != null)) flow.Controls.Add(button);
            if (flow.Controls.Count > 0) root.Controls.Add(flow);
        }

        private static Label Note(string text, Color color) =>
            new Label { Text = text, AutoSize = true, MaximumSize = new Size(340, 0), ForeColor = color, Margin = new Padding(12, 2, 3, 2) };

        private static Button Button(string text, Action action)
        {
            var button = new Button { Text = text, AutoSize = true };
            button.Click += (s, e) => action();
            return button;
        }
    }
}
