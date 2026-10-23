using System;
using System.Globalization;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Profiles.Core;

namespace Profiles.AutoCAD
{
    /// <summary>Creates an unattached, nonannotative horizontal dimension in the plan's local XY plane.</summary>
    internal static class DrawingDimension
    {
        private static readonly int[] LineWeights =
            { 0, 5, 9, 13, 15, 18, 20, 25, 30, 35, 40, 50, 53, 60, 70, 80, 90, 100, 106, 120, 140, 158, 200, 211 };

        internal static RotatedDimension Create(DrawDimension item, Database database)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            if (database == null) throw new ArgumentNullException(nameof(database));
            CheckPoint(item.FirstPoint, "Начало размера");
            CheckPoint(item.SecondPoint, "Конец размера");
            CheckPoint(item.DimensionLinePoint, "Положение размерной линии");
            Positive(item.TextHeight, "Высота текста размера");
            Positive(item.TextWidthFactor, "Коэффициент ширины текста размера");
            Positive(item.MeasurementFactor, "Коэффициент измерения размера");
            double span = Math.Abs(item.SecondPoint.X - item.FirstPoint.X);
            Positive(span, "Горизонтальная длина размера");
            Positive(span * item.MeasurementFactor, "Измеряемая длина размера");
            if (item.DecimalPlaces < 0 || item.DecimalPlaces > 8)
                throw new ProfileValidationException("Точность размера должна быть от 0 до 8 знаков после запятой.");
            if (!Finite(item.LineWeightMm) || item.LineWeightMm < 0)
                throw new ProfileValidationException("Толщина линий размера должна быть конечным неотрицательным числом.");
            if (item.ColorIndex < 0 || item.ColorIndex > 256)
                throw new ProfileValidationException("Недопустимый индекс цвета размера.");

            RotatedDimension dimension = null;
            try
            {
                dimension = new RotatedDimension();
                dimension.SetDatabaseDefaults(database);
                dimension.DimensionStyle = database.Dimstyle;
                dimension.Annotative = AnnotativeStates.False;
                dimension.Normal = Vector3d.ZAxis;
                dimension.Elevation = 0;
                dimension.Rotation = 0;
                dimension.Oblique = 0;
                dimension.XLine1Point = Point(item.FirstPoint);
                dimension.XLine2Point = Point(item.SecondPoint);
                dimension.DimLinePoint = Point(item.DimensionLinePoint);
                dimension.UsingDefaultTextPosition = true;
                dimension.HorizontalRotation = 0;
                dimension.TextRotation = 0;

                // Geometry and heights already use drawing units. DIMLFAC alone
                // converts the measured horizontal span to metres.
                dimension.Dimscale = 1;
                dimension.Dimlfac = item.MeasurementFactor;
                dimension.Dimlunit = 2; // Decimal units.
                dimension.Dimdec = item.DecimalPlaces;
                dimension.Dimdsep = '.';
                dimension.Dimrnd = 0;
                dimension.Dimzin = 0;
                dimension.Dimpost = "";
                dimension.Dimapost = "";
                dimension.Dimmzf = 1;
                dimension.Dimmzs = "";
                dimension.Dimalt = false;
                dimension.Dimtol = false;
                dimension.Dimlim = false;
                dimension.Dimtp = 0;
                dimension.Dimtm = 0;
                dimension.Dimtzin = 0;
                dimension.Dimtfill = 0;

                // TextStyleId is Dimension's managed equivalent of DIMTXSTY.
                dimension.TextStyleId = VariableHeightTextStyle(database);
                dimension.Dimtxt = item.TextHeight;
                dimension.Dimtxtdirection = false;
                dimension.DimensionText = "{\\W" + Number(item.TextWidthFactor) + ";<>}";
                // Keep both the measurement and DIMTXT live, so ordinary grip
                // edits and the Properties text-height control work normally.
                dimension.Dimtad = 1;
                dimension.Dimjust = 0;
                dimension.Dimtih = true;
                dimension.Dimtoh = true;
                dimension.Dimtix = false;
                dimension.Dimatfit = 3;
                dimension.Dimtmove = 2;
                dimension.Dimupt = false;
                dimension.Dimtofl = true;
                dimension.Dimsoxd = false;
                dimension.Dimgap = item.TextHeight * 0.25;
                dimension.Dimtvp = 0;

                // Explicit diagonal engineering ticks avoid inheriting arbitrary
                // arrowhead blocks from the active drawing's dimension style.
                dimension.Dimsah = false;
                dimension.Dimblk = ObjectId.Null;
                dimension.Dimblk1 = ObjectId.Null;
                dimension.Dimblk2 = ObjectId.Null;
                dimension.Dimasz = item.TextHeight * 0.6;
                dimension.Dimtsz = item.TextHeight * 0.3;
                dimension.Dimdle = item.TextHeight * 0.2;
                dimension.Dimexe = item.TextHeight * 0.3;
                dimension.Dimexo = item.TextHeight * 0.15;
                dimension.DimfxlenOn = false;
                dimension.Dimsd1 = dimension.Dimsd2 = false;
                dimension.Dimse1 = dimension.Dimse2 = false;

                var color = Color.FromColorIndex(ColorMethod.ByAci, item.ColorIndex);
                var weight = ClosestLineWeight(item.LineWeightMm);
                dimension.Color = color;
                dimension.Dimclrd = dimension.Dimclre = dimension.Dimclrt = color;
                dimension.LineWeight = weight;
                dimension.Dimlwd = dimension.Dimlwe = weight;
                dimension.LinetypeId = database.ContinuousLinetype;
                dimension.Dimltype = dimension.Dimltex1 = dimension.Dimltex2 = database.ContinuousLinetype;
                dimension.LinetypeScale = 1;
                return dimension;
            }
            catch
            {
                dimension?.Dispose();
                throw;
            }
        }

        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        private static ObjectId VariableHeightTextStyle(Database database)
        {
            var transaction = database.TransactionManager.TopTransaction;
            if (transaction == null)
                throw new InvalidOperationException("Создание размера требует активной транзакции чертежа.");
            var source = (TextStyleTableRecord)transaction.GetObject(database.Textstyle, OpenMode.ForRead);
            if (source.TextSize == 0) return source.ObjectId;

            // A fixed-height text style overrides DIMTXT. Copy only that style
            // with a zero fixed height; never change the user's original style.
            // Reuse compatible copies across rebuilds without resetting any
            // existing style that the user has subsequently edited.
            var table = (TextStyleTable)transaction.GetObject(database.TextStyleTableId, OpenMode.ForRead);
            string prefix = "PROF_DIM_TEXT_" + source.Handle;
            for (int suffix = 0; ; suffix++)
            {
                string name = prefix + (suffix == 0 ? "" : "_" + suffix.ToString(CultureInfo.InvariantCulture));
                if (table.Has(name))
                {
                    var existing = (TextStyleTableRecord)transaction.GetObject(table[name], OpenMode.ForRead);
                    if (SameVariableHeightStyle(source, existing)) return existing.ObjectId;
                    continue;
                }
                if (!table.IsWriteEnabled) table.UpgradeOpen();
                using (var copy = (TextStyleTableRecord)source.Clone())
                {
                    copy.Name = name;
                    copy.TextSize = 0;
                    ObjectId id = table.Add(copy);
                    transaction.AddNewlyCreatedDBObject(copy, true);
                    return id;
                }
            }
        }

        private static bool SameVariableHeightStyle(TextStyleTableRecord source, TextStyleTableRecord candidate)
        {
            var sourceFont = source.Font;
            var candidateFont = candidate.Font;
            return candidate.TextSize == 0 && candidate.XScale == source.XScale &&
                candidate.ObliquingAngle == source.ObliquingAngle && candidate.FlagBits == source.FlagBits &&
                candidate.IsVertical == source.IsVertical && candidate.IsShapeFile == source.IsShapeFile &&
                string.Equals(candidate.FileName, source.FileName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.BigFontFileName, source.BigFontFileName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidateFont.TypeFace, sourceFont.TypeFace, StringComparison.OrdinalIgnoreCase) &&
                candidateFont.Bold == sourceFont.Bold && candidateFont.Italic == sourceFont.Italic &&
                candidateFont.CharacterSet == sourceFont.CharacterSet && candidateFont.PitchAndFamily == sourceFont.PitchAndFamily;
        }

        private static void Positive(double value, string name)
        {
            if (!Finite(value) || value <= 0)
                throw new ProfileValidationException(name + " должна быть конечным положительным числом.");
        }

        private static void CheckPoint(Point2 point, string name)
        {
            if (!Finite(point.X) || !Finite(point.Y))
                throw new ProfileValidationException(name + ": координаты должны быть конечными числами.");
        }

        private static Point3d Point(Point2 point) => new Point3d(point.X, point.Y, 0);
        private static string Number(double value) => value.ToString("0.##############################", CultureInfo.InvariantCulture);

        private static LineWeight ClosestLineWeight(double millimetres)
        {
            double desired = millimetres * 100;
            int nearest = LineWeights[0];
            foreach (int candidate in LineWeights)
                if (Math.Abs(candidate - desired) < Math.Abs(nearest - desired)) nearest = candidate;
            return (LineWeight)nearest;
        }
    }
}
