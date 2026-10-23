using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using Profiles.Core;

namespace Profiles.AutoCAD.UI
{
    internal sealed partial class ProfilePreview
    {
        // The preview expands the dimension only while painting. The drawing
        // plan and AutoCAD output retain one native, editable dimension entity.
        private static DrawText DimensionPreviewText(DrawDimension dimension)
        {
            return new DrawText
            {
                Position = new Point2((dimension.FirstPoint.X + dimension.SecondPoint.X) / 2,
                    dimension.DimensionLinePoint.Y + .75 * dimension.TextHeight),
                Text = (Math.Abs(dimension.SecondPoint.X - dimension.FirstPoint.X) * dimension.MeasurementFactor)
                    .ToString("F" + dimension.DecimalPlaces, CultureInfo.InvariantCulture),
                Height = dimension.TextHeight,
                WidthFactor = dimension.TextWidthFactor,
                Alignment = TextAlignment.Center
            };
        }

        private static IEnumerable<Point2> DimensionPreviewBounds(DrawDimension dimension)
        {
            DrawText text = DimensionPreviewText(dimension);
            double halfWidth = text.Text.Length * text.Height * .75 * text.WidthFactor / 2;
            double left = Math.Min(Math.Min(dimension.FirstPoint.X, dimension.SecondPoint.X), text.Position.X - halfWidth);
            double right = Math.Max(Math.Max(dimension.FirstPoint.X, dimension.SecondPoint.X), text.Position.X + halfWidth);
            yield return dimension.FirstPoint;
            yield return dimension.SecondPoint;
            yield return new Point2(left - dimension.TextHeight, dimension.DimensionLinePoint.Y - dimension.TextHeight);
            yield return new Point2(right + dimension.TextHeight, dimension.DimensionLinePoint.Y + 2 * dimension.TextHeight);
        }

        private void DrawDimensionPreview(Graphics graphics, DrawDimension dimension, Color color)
        {
            double y = dimension.DimensionLinePoint.Y;
            double height = dimension.TextHeight;
            using (var pen = new Pen(color, (float)Math.Max(.65, Math.Min(2.2, dimension.LineWeightMm * 3))))
            {
                graphics.DrawLine(pen,
                    Project(new Point2(Math.Min(dimension.FirstPoint.X, dimension.SecondPoint.X) - .2 * height, y)),
                    Project(new Point2(Math.Max(dimension.FirstPoint.X, dimension.SecondPoint.X) + .2 * height, y)));
                foreach (Point2 point in new[] { dimension.FirstPoint, dimension.SecondPoint })
                {
                    double direction = y >= point.Y ? 1 : -1;
                    graphics.DrawLine(pen,
                        Project(new Point2(point.X, point.Y + direction * .15 * height)),
                        Project(new Point2(point.X, y + direction * .3 * height)));
                    // Engineering ticks match the explicit native DIMTSZ style.
                    double tick = .3 * height;
                    graphics.DrawLine(pen, Project(new Point2(point.X - tick, y - tick)),
                        Project(new Point2(point.X + tick, y + tick)));
                }
            }
            DrawLabel(graphics, DimensionPreviewText(dimension), color);
        }
    }
}
