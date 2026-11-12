using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Kzm.AutoCAD.Drawing;
using AcadApplication = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Kzm.AutoCAD.UI
{
    internal sealed class ReportRow
    {
        internal string Result { get; set; }
        internal string Reason { get; set; }
        internal string Text { get; set; }
        internal string Elevation { get; set; }
        internal string X { get; set; }
        internal string Y { get; set; }
        internal string XySource { get; set; }
        internal string Handle { get; set; }
        internal bool Problem { get; set; }
        internal List<ObjectId> Targets { get; } = new List<ObjectId>();
    }

    internal sealed class Report
    {
        internal string Title { get; set; }
        internal List<string> Summary { get; } = new List<string>();
        internal List<ReportRow> Rows { get; } = new List<ReportRow>();
        internal double ZoomSize { get; set; } = 10;
    }

    /// <summary>Last report per drawing; KZM_REPORT reopens it.</summary>
    internal static class Reports
    {
        private static readonly ConditionalWeakTable<Document, Report> Last = new ConditionalWeakTable<Document, Report>();
        private static ReportWindow window;

        internal static void Show(Document document, Report report)
        {
            Last.Remove(document);
            Last.Add(document, report);
            if (window == null || window.IsDisposed)
            {
                window = new ReportWindow();
                window.ShowReport(document, report);
                AcadApplication.ShowModelessDialog(window);
            }
            else
            {
                window.ShowReport(document, report);
                window.Show();
                window.Activate();
            }
        }

        internal static bool ShowLast(Document document)
        {
            if (!Last.TryGetValue(document, out var report)) return false;
            Show(document, report);
            return true;
        }
    }

    internal sealed class ReportWindow : Form
    {
        private readonly TextBox summary = new TextBox();
        private readonly CheckBox onlyProblems = new CheckBox();
        private readonly DataGridView grid = new DataGridView();
        private Document document;
        private Report report;

        internal ReportWindow()
        {
            Text = "КЗМ — отчёт";
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(980, 560);
            MinimumSize = new Size(640, 360);
            ShowInTaskbar = false;

            summary.Multiline = true;
            summary.ReadOnly = true;
            summary.ScrollBars = ScrollBars.Vertical;
            summary.Dock = DockStyle.Top;
            summary.Height = 130;

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(4), WrapContents = false };
            onlyProblems.Text = "Только проблемные";
            onlyProblems.AutoSize = true;
            onlyProblems.Checked = true;
            onlyProblems.CheckedChanged += (s, e) => Fill();
            var show = new Button { Text = "Показать на чертеже", AutoSize = true };
            show.Click += (s, e) => ShowSelected();
            var copy = new Button { Text = "Копировать таблицу", AutoSize = true };
            copy.Click += (s, e) => CopyTable();
            var close = new Button { Text = "Закрыть", AutoSize = true };
            close.Click += (s, e) => Hide();
            bar.Controls.AddRange(new Control[] { onlyProblems, show, copy, close });

            grid.Dock = DockStyle.Fill;
            grid.ReadOnly = true;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.RowHeadersVisible = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            foreach (var (name, title, weight) in new[]
            {
                ("result", "Результат", 10), ("reason", "Причина", 30), ("text", "Текст", 22), ("z", "Отметка", 9),
                ("x", "X", 12), ("y", "Y", 12), ("xy", "XY", 11), ("handle", "Объект", 8),
            })
                grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = title, FillWeight = weight });
            grid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) ShowSelected(); };

            Controls.Add(grid);
            Controls.Add(bar);
            Controls.Add(summary);
            FormClosing += (s, e) =>
            {
                if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
            };
        }

        internal void ShowReport(Document target, Report value)
        {
            document = target;
            report = value;
            Text = "КЗМ — " + value.Title;
            summary.Text = string.Join(Environment.NewLine, value.Summary);
            Fill();
        }

        private void Fill()
        {
            grid.Rows.Clear();
            if (report == null) return;
            foreach (var row in report.Rows.Where(row => !onlyProblems.Checked || row.Problem))
            {
                int index = grid.Rows.Add(row.Result, row.Reason, row.Text, row.Elevation, row.X, row.Y, row.XySource, row.Handle);
                grid.Rows[index].Tag = row;
                if (row.Problem) grid.Rows[index].DefaultCellStyle.ForeColor = Color.Firebrick;
            }
        }

        private void ShowSelected()
        {
            if (grid.CurrentRow?.Tag is not ReportRow row || row.Targets.Count == 0) return;
            var active = AcadApplication.DocumentManager.MdiActiveDocument;
            if (active == null || !ReferenceEquals(active, document))
            {
                MessageBox.Show(this, "Отчёт относится к другому чертежу. Перейдите в этот чертёж и повторите.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                using (active.LockDocument())
                using (var transaction = active.TransactionManager.StartTransaction())
                {
                    var alive = row.Targets.Where(id => id.IsValid && !id.IsErased).ToList();
                    if (alive.Count == 0)
                    {
                        MessageBox.Show(this, "Объект удалён из чертежа.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    Views.ZoomToObjects(active.Editor, transaction, alive, report.ZoomSize);
                    transaction.Commit();
                    active.Editor.SetImpliedSelection(alive.ToArray());
                }
            }
            catch (System.Exception exception)
            {
                MessageBox.Show(this, "Не удалось показать объект: " + exception.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void CopyTable()
        {
            if (report == null) return;
            var text = new StringBuilder();
            text.AppendLine(string.Join("\t", grid.Columns.Cast<DataGridViewColumn>().Select(column => column.HeaderText)));
            foreach (var row in report.Rows.Where(row => !onlyProblems.Checked || row.Problem))
                text.AppendLine(string.Join("\t", row.Result, row.Reason, row.Text, row.Elevation, row.X, row.Y, row.XySource, row.Handle));
            Clipboard.SetText(text.ToString());
        }
    }
}
