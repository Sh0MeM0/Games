using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Kzm.AutoCAD.Services;
using Kzm.Core;
using Font = System.Drawing.Font;

namespace Kzm.AutoCAD.UI.Panel
{
    /// <summary>
    /// Плоскость (задание п. 2.3): по трём точкам или по точке и двум уклонам в промилле.
    /// Точки с отметкой плоскости ставятся указанием или в вершинах полилинии (контур площадки).
    /// </summary>
    internal sealed class PlaneView : UserControl, IPanelView
    {
        private readonly Label title = new Label { AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold), Margin = new Padding(3, 6, 3, 3) };
        private readonly RadioButton byThree = new RadioButton { Text = "По трём точкам", AutoSize = true, Checked = true };
        private readonly RadioButton bySlopes = new RadioButton { Text = "По точке и уклонам", AutoSize = true };
        private readonly TextBox[,] three = new TextBox[3, 3];
        private readonly TextBox baseX = Box(), baseY = Box(), baseZ = Box(70);
        private readonly TextBox direction = Box(70), along = Box(70), across = Box(70);
        private readonly TableLayoutPanel threePanel = new TableLayoutPanel { ColumnCount = 5, AutoSize = true };
        private readonly TableLayoutPanel slopesPanel = new TableLayoutPanel { ColumnCount = 2, AutoSize = true };
        private readonly Label result = new Label { AutoSize = true, MaximumSize = new Size(340, 0) };
        private readonly CheckBox contourLine = new CheckBox
        {
            Text = "Контур — структурная линия (внутри контура плоскость точная)", AutoSize = true, Checked = true, MaximumSize = new Size(340, 0),
        };
        private KzmPanel panel;
        private string surfaceId;

        private static TextBox Box(int width = 100) => new TextBox { Width = width };

        internal PlaneView()
        {
            var root = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = System.Windows.Forms.FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(4) };
            root.Controls.Add(title);
            root.Controls.Add(Flow(byThree, bySlopes));

            threePanel.Controls.Add(new Label { Text = string.Empty, AutoSize = true });
            foreach (var header in new[] { "X", "Y", "Отметка" }) threePanel.Controls.Add(new Label { Text = header, AutoSize = true });
            threePanel.Controls.Add(new Label { Text = string.Empty, AutoSize = true });
            for (int i = 0; i < 3; i++)
            {
                threePanel.Controls.Add(new Label { Text = "Точка " + (i + 1), AutoSize = true, Margin = new Padding(3, 7, 3, 0) });
                for (int k = 0; k < 3; k++)
                {
                    three[i, k] = Box(k == 2 ? 70 : 95);
                    three[i, k].TextChanged += (s, e) => Describe();
                    threePanel.Controls.Add(three[i, k]);
                }
                int row = i;
                var pick = new Button { Text = "…", Width = 28 };
                pick.Click += (s, e) => PickPoint(row);
                threePanel.Controls.Add(pick);
            }
            var pickAll = new Button { Text = "Указать три точки…", AutoSize = true };
            pickAll.Click += (s, e) => PickThree();
            root.Controls.Add(threePanel);
            root.Controls.Add(pickAll);

            void Row(string caption, Control control)
            {
                slopesPanel.Controls.Add(new Label { Text = caption, AutoSize = true, MaximumSize = new Size(170, 0), Margin = new Padding(3, 7, 6, 3) });
                slopesPanel.Controls.Add(control);
            }
            var pickBase = new Button { Text = "Указать…", AutoSize = true };
            pickBase.Click += (s, e) => PickBase();
            Row("Точка X / Y", Flow(baseX, baseY));
            Row("Отметка в точке", Flow(baseZ, pickBase));
            var pickDirection = new Button { Text = "По двум точкам…", AutoSize = true };
            pickDirection.Click += (s, e) => PickDirection();
            Row("Направление уклона 1, °", Flow(direction, pickDirection));
            Row("Уклон вдоль, ‰ (+ подъём)", along);
            Row("Уклон поперёк (влево), ‰", across);
            foreach (var box in new[] { baseX, baseY, baseZ, direction, along, across }) box.TextChanged += (s, e) => Describe();
            direction.Text = "0";
            along.Text = "0";
            across.Text = "0";
            root.Controls.Add(slopesPanel);

            root.Controls.Add(result);
            var points = new Button { Text = "Точки указанием…", AutoSize = true };
            points.Click += (s, e) => CreatePoints();
            var vertices = new Button { Text = "В вершинах полилинии…", AutoSize = true };
            vertices.Click += (s, e) => CreateOnPolyline();
            root.Controls.Add(Flow(points, vertices));
            root.Controls.Add(contourLine);
            var back = new Button { Text = "К поверхности", AutoSize = true };
            back.Click += (s, e) => panel?.ShowSurface(surfaceId);
            root.Controls.Add(back);
            root.Controls.Add(new Label
            {
                AutoSize = true, MaximumSize = new Size(340, 0), ForeColor = SystemColors.GrayText,
                Text = "Точку для плоскости укажите на чертеже: если там стоит точка модуля (привязка «Точка вставки»), берётся её отметка, " +
                       "иначе отметку спросят в командной строке. Направление — градусы от оси X против часовой стрелки; уклон поперёк — влево от направления. " +
                       "Уклоны вводятся в промилле (‰): 5 ‰ = 0,005.",
            });
            Controls.Add(root);
            byThree.CheckedChanged += (s, e) => ModeChanged();
            ModeChanged();
        }

        private static FlowLayoutPanel Flow(params Control[] controls)
        {
            var flow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = Padding.Empty, MaximumSize = new Size(350, 0) };
            flow.Controls.AddRange(controls);
            return flow;
        }

        private void ModeChanged()
        {
            threePanel.Enabled = byThree.Checked;
            slopesPanel.Enabled = bySlopes.Checked;
            Describe();
        }

        public void Bind(KzmPanel owner, string id)
        {
            panel = owner;
            surfaceId = id;
            var surface = panel.Snapshot.Find(id);
            title.Text = "Плоскость — точки в поверхность «" + (surface?.Info.Name ?? "?") + "»";
            Describe();
        }

        /// <summary>The plane from the form, or null with the reason in <paramref name="problem"/>.</summary>
        private PlaneDefinition Read(out string problem)
        {
            problem = null;
            try
            {
                if (byThree.Checked)
                {
                    var values = new double[3, 3];
                    for (int i = 0; i < 3; i++)
                    for (int k = 0; k < 3; k++)
                        if (!ElevationFormat.TryParse(three[i, k].Text, out values[i, k]))
                        {
                            problem = "заполните X, Y и отметку трёх точек (или укажите их на чертеже)";
                            return null;
                        }
                    return PlaneDefinition.FromThreePoints(new XY(values[0, 0], values[0, 1]), values[0, 2], new XY(values[1, 0], values[1, 1]), values[1, 2],
                        new XY(values[2, 0], values[2, 1]), values[2, 2]);
                }
                var numbers = new[] { baseX, baseY, baseZ, direction, along, across }.Select(box =>
                    ElevationFormat.TryParse(box.Text, out double value) ? value : (double?)null).ToArray();
                if (numbers.Any(n => !n.HasValue))
                {
                    problem = "заполните точку, отметку, направление и оба уклона";
                    return null;
                }
                return PlaneDefinition.FromSlopes(new XY(numbers[0].Value, numbers[1].Value), numbers[2].Value, numbers[3].Value, numbers[4].Value, numbers[5].Value);
            }
            catch (KzmException exception)
            {
                problem = exception.Message;
                return null;
            }
        }

        private void Describe()
        {
            var plane = Read(out string problem);
            int decimals = panel?.Snapshot.Settings.Decimals ?? KzmSettings.DefaultDecimals;
            result.Text = plane != null ? "Плоскость: " + plane.Describe(decimals) + "." : "Плоскость не задана: " + problem + ".";
            result.ForeColor = plane != null ? SystemColors.ControlText : Color.Firebrick;
        }

        private void Fill(int row, (XY Point, double Z) value)
        {
            three[row, 0].Text = SettingsView.FormatValue(Math.Round(value.Point.X, 3));
            three[row, 1].Text = SettingsView.FormatValue(Math.Round(value.Point.Y, 3));
            three[row, 2].Text = SettingsView.FormatValue(Math.Round(value.Z, 4));
        }

        private void PickPoint(int row)
        {
            (XY, double)? picked = null;
            var settings = panel.Snapshot.Settings;
            PanelActions.Pick("точка плоскости", document => picked = SurfaceTools.PickElevatedPoint(document, "Точка " + (row + 1) + " плоскости", settings),
                () => { if (picked.HasValue) Fill(row, picked.Value); });
        }

        private void PickThree()
        {
            var picked = new List<(XY, double)>();
            var settings = panel.Snapshot.Settings;
            PanelActions.Pick("три точки плоскости", document =>
            {
                for (int i = 0; i < 3; i++)
                {
                    var value = SurfaceTools.PickElevatedPoint(document, "Точка " + (i + 1) + " плоскости", settings);
                    if (!value.HasValue) return;
                    picked.Add(value.Value);
                }
            }, () =>
            {
                for (int i = 0; i < picked.Count; i++) Fill(i, picked[i]);
                if (picked.Count == 3) PanelMessages.Info("Три точки плоскости заданы.");
            });
        }

        private void PickBase()
        {
            (XY Point, double Z)? picked = null;
            var settings = panel.Snapshot.Settings;
            PanelActions.Pick("точка плоскости", document => picked = SurfaceTools.PickElevatedPoint(document, "Точка плоскости", settings), () =>
            {
                if (!picked.HasValue) return;
                baseX.Text = SettingsView.FormatValue(Math.Round(picked.Value.Point.X, 3));
                baseY.Text = SettingsView.FormatValue(Math.Round(picked.Value.Point.Y, 3));
                baseZ.Text = SettingsView.FormatValue(Math.Round(picked.Value.Z, 4));
            });
        }

        private void PickDirection()
        {
            double? degrees = null;
            PanelActions.Pick("направление уклона", document =>
            {
                var first = document.Editor.GetPoint(new PromptPointOptions("\nНачало направления уклона 1: "));
                if (first.Status != PromptStatus.OK) return;
                var second = document.Editor.GetPoint(new PromptPointOptions("\nКонец направления: ") { BasePoint = first.Value, UseBasePoint = true });
                if (second.Status != PromptStatus.OK) return;
                var ucs = document.Editor.CurrentUserCoordinateSystem;
                var a = first.Value.TransformBy(ucs);
                var b = second.Value.TransformBy(ucs);
                degrees = Math.Atan2(b.Y - a.Y, b.X - a.X) * 180 / Math.PI;
            }, () => { if (degrees.HasValue) direction.Text = SettingsView.FormatValue(Math.Round(degrees.Value, 4)); });
        }

        private void CreatePoints()
        {
            var plane = Read(out string problem);
            if (plane == null) { PanelMessages.Error(problem + "."); return; }
            string id = surfaceId;
            var settings = panel.Snapshot.Settings;
            PanelActions.RunInteractive("точки на плоскости", document => SurfaceTools.CreatePlanePoints(document, id, plane, settings), panel.RefreshAll);
        }

        private void CreateOnPolyline()
        {
            var plane = Read(out string problem);
            if (plane == null) { PanelMessages.Error(problem + "."); return; }
            string id = surfaceId;
            bool makeLine = contourLine.Checked;
            var settings = panel.Snapshot.Settings;
            PanelActions.RunInteractive("точки на плоскости", document =>
            {
                var options = new PromptEntityOptions("\nУкажите полилинию — контур площадки (в её вершинах будут точки на плоскости): ");
                options.SetRejectMessage("\nНужна полилиния.");
                options.AddAllowedClass(typeof(Polyline), true);
                options.AddAllowedClass(typeof(Polyline2d), true);
                options.AddAllowedClass(typeof(Polyline3d), true);
                var pick = document.Editor.GetEntity(options);
                if (pick.Status != PromptStatus.OK) return;
                var (created, changed) = SurfaceTools.PlanePointsOnPolyline(document.Database, id, plane, pick.ObjectId, makeLine, settings);
                PanelMessages.Info("Точки на плоскости: создано " + created + (changed > 0 ? ", отметки изменены у " + changed : string.Empty) +
                                   (makeLine ? "; контур стал структурной линией" : string.Empty) + ". Отменить — U.");
            }, panel.RefreshAll);
        }
    }
}
