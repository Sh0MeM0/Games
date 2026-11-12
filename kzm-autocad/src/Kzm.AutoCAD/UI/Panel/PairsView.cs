using System;
using System.Collections.Generic;
using System.Drawing;
using Font = System.Drawing.Font;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using Kzm.AutoCAD.Commands;
using Kzm.AutoCAD.Drawing;
using Kzm.AutoCAD.Services;
using Kzm.Core;

namespace Kzm.AutoCAD.UI.Panel
{
    /// <summary>Pairs of points closer than the tolerance; the user decides which point stays.</summary>
    internal sealed class PairsView : UserControl, IPanelView
    {
        private readonly Label title = new Label { AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold), Dock = DockStyle.Top, Margin = new Padding(3, 6, 3, 3) };
        private readonly DataGridView grid = new DataGridView();
        private readonly List<DBText> labels = new List<DBText>();
        private KzmPanel panel;
        private List<PairInfo> pairs = new List<PairInfo>();

        internal PairsView()
        {
            grid.Dock = DockStyle.Fill;
            grid.ReadOnly = true;
            grid.AllowUserToAddRows = false;
            grid.RowHeadersVisible = false;
            grid.MultiSelect = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            foreach (var name in new[] { "№", "Расстояние, м", "Отметка 1", "Отметка 2", "Объекты" })
                grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = name });
            grid.SelectionChanged += (s, e) => ShowCurrent();

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
            buttons.Controls.Add(Action("Оставить 1", PairDecision.KeepFirst));
            buttons.Controls.Add(Action("Оставить 2", PairDecision.KeepSecond));
            buttons.Controls.Add(Action("Оставить обе", PairDecision.KeepBoth));
            buttons.Controls.Add(Action("Удалить обе", PairDecision.DeleteBoth));
            var hint = new Label
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                MaximumSize = new Size(340, 0),
                Padding = new Padding(3),
                Text = "Выберите пару — чертёж покажет её, точки подписаны «1» и «2». Решение по паре — одной кнопкой; отменяется командой U.",
            };
            Controls.Add(grid);
            Controls.Add(buttons);
            Controls.Add(hint);
            Controls.Add(title);
        }

        private Button Action(string text, PairDecision decision)
        {
            var button = new Button { Text = text, AutoSize = true };
            button.Click += (s, e) => Decide(decision);
            return button;
        }

        public void Bind(KzmPanel owner, string surfaceId)
        {
            panel = owner;
            ClearLabels();
            var surface = panel.Snapshot.Find(surfaceId);
            pairs = surface == null || panel.Document == null
                ? new List<PairInfo>()
                : DrawingData.ReadPairs(panel.Document, panel.Snapshot.Settings, surfaceId);
            var settings = panel.Snapshot.Settings;
            title.Text = surface == null ? "Поверхность не найдена"
                : "Близкие точки «" + surface.Info.Name + "»: " + pairs.Count + " (ближе " +
                  ElevationFormat.Format(KzmSettings.DuplicateTolerance, Math.Max(2, settings.Decimals)) + " м)";
            grid.Rows.Clear();
            for (int i = 0; i < pairs.Count; i++)
            {
                var pair = pairs[i];
                int index = grid.Rows.Add(i + 1, ElevationFormat.Format(pair.Distance, 3), Elevation(pair.First, settings),
                    Elevation(pair.Second, settings), pair.First.Handle + " / " + pair.Second.Handle);
                grid.Rows[index].Tag = pair;
            }
        }

        private static string Elevation(SurfacePoint point, KzmSettings settings) =>
            point.Elevation.IsValid ? ElevationFormat.Format(point.Elevation.Value.Value, settings.Decimals) : "?";

        private PairInfo Current => grid.CurrentRow?.Tag as PairInfo;

        private void ShowCurrent()
        {
            ClearLabels();
            var pair = Current;
            if (pair == null || panel?.Document == null) return;
            double textHeight = panel.Snapshot.Find(pair.First.Data.SurfaceId)?.Info.Appearance.TextHeight ?? 1;
            PanelActions.Show(panel.Document, new[] { pair.First.Id, pair.Second.Id }, KzmCommands.ZoomSize(textHeight));
            double height = textHeight * 1.5;
            // The two points are almost at one place: labels go to opposite sides.
            labels.Add(Label("1", pair.First.Position, height, -2.0, 0.5));
            labels.Add(Label("2", pair.Second.Position, height, 0.7, -1.7));
            var manager = TransientManager.CurrentTransientManager;
            foreach (var label in labels) manager.AddTransient(label, TransientDrawingMode.DirectTopmost, 128, new IntegerCollection());
        }

        private static DBText Label(string text, XY at, double height, double dx, double dy) => new DBText
        {
            TextString = text,
            Height = height,
            Position = new Point3d(at.X + height * dx, at.Y + height * dy, 0),
            Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(ColorMethod.ByAci, 2),
        };

        /// <summary>Removes the «1»/«2» labels from the screen.</summary>
        internal void ClearLabels()
        {
            if (labels.Count == 0) return;
            var manager = TransientManager.CurrentTransientManager;
            foreach (var label in labels)
            {
                try { manager.EraseTransient(label, new IntegerCollection()); } catch (System.Exception) { }
                label.Dispose();
            }
            labels.Clear();
        }

        private void Decide(PairDecision decision)
        {
            var pair = Current;
            if (pair == null)
            {
                PanelMessages.Error("выберите пару в таблице.");
                return;
            }
            ClearLabels();
            var stored = pair.Pair;
            PanelActions.Run("близкие точки", document =>
            {
                int erased = EditServices.ResolvePair(document.Database, stored, decision, panel.Snapshot.Settings);
                PanelMessages.Info("Пара решена" + (erased > 0 ? ", удалено точек: " + erased : ", обе точки оставлены") + ".");
            }, panel.RefreshAll);
        }
    }
}
