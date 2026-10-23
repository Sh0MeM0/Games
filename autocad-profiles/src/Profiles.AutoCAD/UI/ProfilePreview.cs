using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using Profiles.Core;

namespace Profiles.AutoCAD.UI
{
    /// <summary>Lightweight local preview of the exact plan sent to the AutoCAD adapter.</summary>
    internal sealed class ProfilePreview : Control
    {
        private DrawingPlan plan;
        private double minX, maxX, minY, maxY;
        private double zoom = 1;
        private PointF pan;
        private Point dragStart;
        private PointF panStart;
        private bool dragging;
        private bool stale;

        public ProfilePreview()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.White;
            TabStop = true;
        }

        public void SetPlan(DrawingPlan value)
        {
            plan = value;
            stale = false;
            ComputeBounds();
            Fit();
        }

        public void MarkStale()
        {
            stale = plan != null;
            Invalidate();
        }

        public void Fit()
        {
            zoom = 1;
            pan = PointF.Empty;
            Invalidate();
        }

        private double PreviewScale => Math.Max(0.00000001, Math.Min(Math.Max(20, ClientSize.Width - 48) / Math.Max(1e-9, maxX - minX),
            Math.Max(20, ClientSize.Height - 48) / Math.Max(1e-9, maxY - minY))) * zoom;

        private PointF Project(Point2 point)
        {
            double scale = PreviewScale;
            return new PointF((float)((point.X - (minX + maxX) / 2) * scale + ClientSize.Width / 2.0 + pan.X),
                (float)(((minY + maxY) / 2 - point.Y) * scale + ClientSize.Height / 2.0 + pan.Y));
        }

        private void ComputeBounds()
        {
            var points = new List<Point2>();
            if (plan != null)
                foreach (var entity in plan.Entities)
                {
                    if (entity is DrawPolyline line) points.AddRange(line.Points);
                    else if (entity is DrawHatch hatch) points.AddRange(hatch.Boundary);
                    else if (entity is DrawCircle circle)
                    {
                        points.Add(new Point2(circle.Center.X - circle.Radius, circle.Center.Y - circle.Radius));
                        points.Add(new Point2(circle.Center.X + circle.Radius, circle.Center.Y + circle.Radius));
                    }
                    else if (entity is DrawText text)
                    {
                        string[] lines = (text.Text ?? "").Split('\n');
                        double width = text.MaxWidth > 0 ? text.MaxWidth : Math.Max(1, lines.Max(s => s.Length)) * text.Height * .65 * text.WidthFactor;
                        double height = Math.Max(1, lines.Length) * text.Height * 1.35;
                        double offset = text.Alignment == TextAlignment.Center ? -width / 2 : text.Alignment == TextAlignment.Right ? -width : 0;
                        double angle = text.RotationDegrees * Math.PI / 180;
                        foreach (var corner in new[] { new Point2(offset, -height / 2), new Point2(offset + width, -height / 2), new Point2(offset, height / 2), new Point2(offset + width, height / 2) })
                            points.Add(new Point2(text.Position.X + corner.X * Math.Cos(angle) - corner.Y * Math.Sin(angle),
                                text.Position.Y + corner.X * Math.Sin(angle) + corner.Y * Math.Cos(angle)));
                    }
                }
            if (points.Count == 0) { minX = minY = 0; maxX = maxY = 1; return; }
            minX = points.Min(p => p.X); maxX = points.Max(p => p.X);
            minY = points.Min(p => p.Y); maxY = points.Max(p => p.Y);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (plan == null)
            {
                TextRenderer.DrawText(e.Graphics, "Введите данные и нажмите «Проверить и показать».\nЗдесь появится профиль, который будет передан в AutoCAD.",
                    Font, ClientRectangle, Color.DimGray, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            foreach (var entity in plan.Entities)
            {
                Color color = EntityColor(entity.ColorIndex);
                if (entity is DrawHatch hatch) DrawRegion(e.Graphics, hatch, color);
                else if (entity is DrawPolyline line && line.Points.Count >= 2)
                {
                    using (var pen = new Pen(color, (float)Math.Max(.65, Math.Min(2.2, entity.LineWeightMm * 3))))
                    {
                        if (entity.Dashed) pen.DashStyle = DashStyle.Dash;
                        var vertices = line.Points.Select(Project).ToArray();
                        e.Graphics.DrawLines(pen, vertices);
                        if (line.Closed) e.Graphics.DrawLine(pen, vertices[vertices.Length - 1], vertices[0]);
                    }
                }
                else if (entity is DrawCircle circle)
                {
                    PointF center = Project(circle.Center);
                    float radius = (float)(circle.Radius * PreviewScale);
                    using (var pen = new Pen(color, .8F)) e.Graphics.DrawEllipse(pen, center.X - radius, center.Y - radius, radius * 2, radius * 2);
                }
                else if (entity is DrawText text) DrawLabel(e.Graphics, text, color);
            }
            if (stale)
            {
                const string message = "Предпросмотр устарел — нажмите «Обновить»";
                var banner = new Rectangle(8, 8, Math.Min(ClientSize.Width - 16, 440), 32);
                using (var brush = new SolidBrush(Color.FromArgb(255, 243, 205))) e.Graphics.FillRectangle(brush, banner);
                TextRenderer.DrawText(e.Graphics, message, Font, banner, Color.FromArgb(110, 75, 0), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }

        private void DrawRegion(Graphics graphics, DrawHatch hatch, Color color)
        {
            if (hatch.Boundary.Count < 3 || hatch.PatternSpacing <= 0) return;
            var state = graphics.Save();
            try
            {
                var vertices = hatch.Boundary.Select(Project).ToArray();
                using (var path = new GraphicsPath())
                using (var pen = new Pen(color, .7F))
                {
                    path.AddPolygon(vertices);
                    graphics.SetClip(path, CombineMode.Intersect);
                    // Work in screen coordinates, so positive mathematical angles turn upwards.
                    double angle = -hatch.PatternAngleDegrees * Math.PI / 180;
                    double dx = Math.Cos(angle), dy = Math.Sin(angle), nx = -dy, ny = dx;
                    double minAlong = vertices.Min(p => p.X * dx + p.Y * dy), maxAlong = vertices.Max(p => p.X * dx + p.Y * dy);
                    double minAcross = vertices.Min(p => p.X * nx + p.Y * ny), maxAcross = vertices.Max(p => p.X * nx + p.Y * ny);
                    // Avoid a pathological number of subpixel lines while zoomed far out.
                    double spacing = Math.Max(2, hatch.PatternSpacing * PreviewScale);
                    double first = Math.Floor(minAcross / spacing) * spacing;
                    for (double across = first; across <= maxAcross; across += spacing)
                        graphics.DrawLine(pen,
                            (float)(minAlong * dx + across * nx), (float)(minAlong * dy + across * ny),
                            (float)(maxAlong * dx + across * nx), (float)(maxAlong * dy + across * ny));
                }
            }
            finally { graphics.Restore(state); }
        }

        private void DrawLabel(Graphics graphics, DrawText text, Color color)
        {
            float height = (float)(text.Height * PreviewScale);
            if (height < 1.0F || height > 2000F || string.IsNullOrEmpty(text.Text)) return;
            var state = graphics.Save();
            try
            {
                var position = Project(text.Position);
                graphics.TranslateTransform(position.X, position.Y);
                graphics.RotateTransform((float)-text.RotationDegrees);
                graphics.ScaleTransform((float)Math.Max(.1, text.WidthFactor), 1);
                using (var font = new Font("Arial", height, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var brush = new SolidBrush(color))
                using (var format = new StringFormat(StringFormat.GenericTypographic))
                {
                    format.Alignment = text.Alignment == TextAlignment.Center ? StringAlignment.Center : text.Alignment == TextAlignment.Right ? StringAlignment.Far : StringAlignment.Near;
                    format.LineAlignment = StringAlignment.Center;
                    if (text.MaxWidth > 0)
                    {
                        float width = (float)(text.MaxWidth * PreviewScale / Math.Max(.1, text.WidthFactor));
                        float left = text.Alignment == TextAlignment.Center ? -width / 2 : text.Alignment == TextAlignment.Right ? -width : 0;
                        float blockHeight = height * Math.Max(1, text.Text.Split('\n').Length + 1) * 1.4F;
                        graphics.DrawString(text.Text, font, brush, new RectangleF(left, -blockHeight / 2, width, blockHeight), format);
                    }
                    else graphics.DrawString(text.Text, font, brush, PointF.Empty, format);
                }
            }
            finally { graphics.Restore(state); }
        }

        private static Color EntityColor(short index)
        {
            switch (index)
            {
                case 1: return Color.Red;
                case 3: return Color.Lime;
                case 5: return Color.Blue;
                case 6: return Color.Magenta;
                case 8: return Color.FromArgb(170, 175, 180);
                case 30: return Color.FromArgb(255, 127, 0);
                default: return Color.Black;
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            double previous = zoom;
            zoom = Math.Max(.2, Math.Min(30, zoom * (e.Delta > 0 ? 1.2 : 1 / 1.2)));
            double ratio = zoom / previous;
            pan = new PointF((float)(e.X - ClientSize.Width / 2.0 - (e.X - ClientSize.Width / 2.0 - pan.X) * ratio),
                (float)(e.Y - ClientSize.Height / 2.0 - (e.Y - ClientSize.Height / 2.0 - pan.Y) * ratio));
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            dragging = true; dragStart = e.Location; panStart = pan;
            Capture = true; Cursor = Cursors.Hand;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!dragging) return;
            pan = new PointF(panStart.X + e.X - dragStart.X, panStart.Y + e.Y - dragStart.Y);
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            dragging = false; Capture = false; Cursor = Cursors.Default;
        }
    }
}
