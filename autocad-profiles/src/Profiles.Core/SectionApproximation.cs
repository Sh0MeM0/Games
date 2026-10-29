using System;
using System.Collections.Generic;
using System.Linq;

namespace Profiles.Core
{
    public enum SectionApproximationShape { SmoothBowl, StraightSlopes }

    /// <summary>Explicit estimates for a draft cross-section. All operations return copies.</summary>
    public static class SectionApproximation
    {
        public static CrossSection DistributeDistances(CrossSection source, double bankWidth,
            double? bottomFromLeft = null, bool preserveEntered = true)
        {
            var result = ProfileInterpolation.CloneSection(source);
            var roles = Roles(result);
            ProfileValidation.Range(bankWidth, 0.000001, 1000000000, "Ширина между берегами");
            double leftWidth = bottomFromLeft ?? bankWidth / 2;
            if (!NumberParser.Finite(leftWidth) || leftWidth <= 0 || leftWidth >= bankWidth)
                throw new ProfileValidationException("Дно должно находиться строго между берегами: расстояние от левого берега больше нуля и меньше общей ширины.");
            if (!result.UsePointDistances)
                for (int i = 0; i < result.Points.Count; i++)
                    result.Points[i].DistanceFromPrevious = i == 0 ? 0 : result.Points[i].Offset - result.Points[i - 1].Offset;
            result.UsePointDistances = true;
            result.Points[0].DistanceFromPrevious = 0;
            DistributeSide(result.Points, roles[0], roles[1], leftWidth, preserveEntered);
            DistributeSide(result.Points, roles[1], roles[2], bankWidth - leftWidth, preserveEntered);
            // Width constrains only the bank span. Missing exterior intervals get
            // the average step of that side; existing exterior measurements stay.
            for (int i = 1; i <= roles[0]; i++)
                if (!result.Points[i].DistanceFromPrevious.HasValue)
                    result.Points[i].DistanceFromPrevious = leftWidth / (roles[1] - roles[0]);
            for (int i = roles[2] + 1; i < result.Points.Count; i++)
                if (!result.Points[i].DistanceFromPrevious.HasValue)
                    result.Points[i].DistanceFromPrevious = (bankWidth - leftWidth) / (roles[2] - roles[1]);
            ResolveOffsets(result);
            return result;
        }

        public static CrossSection FillElevations(CrossSection source, SectionApproximationShape shape,
            IList<LongitudinalPoint> longitudinal = null)
        {
            if (!Enum.IsDefined(typeof(SectionApproximationShape), shape))
                throw new ProfileValidationException("Неизвестная форма поперечного профиля.");
            var result = ProfileInterpolation.CloneSection(source);
            var roles = Roles(result);
            if (result.Points.Any(p => p.UseLongitudinalElevation))
            {
                if (!result.Station.HasValue || longitudinal == null)
                    throw new ProfileValidationException("Для связанных отметок нужен пикет и текущий продольный профиль.");
                var sample = ProfileInterpolation.SampleLongitudinal(longitudinal, result.Station.Value);
                foreach (var point in result.Points.Where(p => p.UseLongitudinalElevation))
                {
                    if (point.Role == SectionPointRole.LeftBank) point.Existing = sample.LeftBank;
                    else if (point.Role == SectionPointRole.Bottom) point.Existing = sample.ExistingBottom;
                    else if (point.Role == SectionPointRole.RightBank) point.Existing = sample.RightBank;
                }
            }
            foreach (int index in roles)
                if (!result.Points[index].Existing.HasValue)
                    throw new ProfileValidationException("Сначала задайте отметки левого берега, существующего дна и правого берега (вручную или на продольном профиле).");
            double bottom = result.Points[roles[1]].Existing.Value;
            if (result.Points[roles[0]].Existing < bottom || result.Points[roles[2]].Existing < bottom)
                throw new ProfileValidationException("Отметка берега ниже назначенного дна. Проверьте роли и опорные отметки; расчёт их не исправляет.");
            ResolveOffsets(result);
            var known = result.Points.Where(p => p.Existing.HasValue).ToList();
            foreach (var point in known) ProfileValidation.Elevation(point.Existing, "Опорная отметка");
            var x = known.Select(p => p.Offset).ToArray();
            var y = known.Select(p => p.Existing.Value).ToArray();
            var slopes = Slopes(x, y);
            int bottomAnchor = known.IndexOf(result.Points[roles[1]]);
            slopes[bottomAnchor] = 0;
            int interval = 0;
            foreach (var point in result.Points)
            {
                if (point.Existing.HasValue) continue;
                double value;
                if (point.Offset < x[0])
                    value = y[0] + (point.Offset - x[0]) * (y[1] - y[0]) / (x[1] - x[0]);
                else if (point.Offset > x[x.Length - 1])
                {
                    int last = x.Length - 1;
                    value = y[last] + (point.Offset - x[last]) * (y[last] - y[last - 1]) / (x[last] - x[last - 1]);
                }
                else
                {
                    while (interval + 1 < x.Length - 1 && point.Offset > x[interval + 1]) interval++;
                    double h = x[interval + 1] - x[interval], t = (point.Offset - x[interval]) / h;
                    if (shape == SectionApproximationShape.StraightSlopes)
                        value = y[interval] + t * (y[interval + 1] - y[interval]);
                    else
                    {
                        double t2 = t * t, t3 = t2 * t;
                        value = (2 * t3 - 3 * t2 + 1) * y[interval] + (t3 - 2 * t2 + t) * h * slopes[interval]
                            + (-2 * t3 + 3 * t2) * y[interval + 1] + (t3 - t2) * h * slopes[interval + 1];
                        // Suppress floating-point overshoot at equal/near-equal anchors.
                        value = Math.Max(Math.Min(y[interval], y[interval + 1]), Math.Min(Math.Max(y[interval], y[interval + 1]), value));
                    }
                }
                ProfileValidation.Elevation(value, "Расчётная отметка");
                point.Existing = value;
            }
            result.LeftBank = result.Points[roles[0]].Existing;
            result.ExistingBottom = result.Points[roles[1]].Existing;
            result.RightBank = result.Points[roles[2]].Existing;
            return result;
        }

        private static int[] Roles(CrossSection source)
        {
            if (source.Points.Count < 3 || source.Points.Count > 10000)
                throw new ProfileValidationException("Для расчёта нужно от 3 до 10000 точек.");
            var roles = new[] { SectionPointRole.LeftBank, SectionPointRole.Bottom, SectionPointRole.RightBank };
            var indexes = new int[3];
            for (int i = 0; i < roles.Length; i++)
            {
                if (source.Points.Count(p => p.Role == roles[i]) != 1)
                    throw new ProfileValidationException("Назначьте ровно один левый берег, одно существующее дно и один правый берег через меню в шапке точек.");
                indexes[i] = source.Points.FindIndex(p => p.Role == roles[i]);
            }
            if (indexes[0] >= indexes[1] || indexes[1] >= indexes[2])
                throw new ProfileValidationException("Порядок точек слева направо должен быть: левый берег, существующее дно, правый берег.");
            return indexes;
        }

        private static void DistributeSide(List<SectionPoint> points, int first, int last, double width, bool preserve)
        {
            double fixedSum = 0;
            var empty = new List<int>();
            for (int i = first + 1; i <= last; i++)
            {
                var distance = points[i].DistanceFromPrevious;
                if (preserve && distance.HasValue)
                {
                    if (!NumberParser.Finite(distance.Value) || distance <= 0)
                        throw new ProfileValidationException("Заполненные расстояния должны быть положительными числами.");
                    fixedSum += distance.Value;
                }
                else empty.Add(i);
            }
            double remainder = width - fixedSum;
            if (empty.Count == 0)
            {
                if (Math.Abs(remainder) > 1e-9 * Math.Max(1, width))
                    throw new ProfileValidationException("Введённые интервалы не совпадают с шириной этой стороны. Уточните положение дна или снимите «Сохранить заполненные интервалы».");
                return;
            }
            if (!NumberParser.Finite(remainder) || remainder <= 0)
                throw new ProfileValidationException("Введённые интервалы занимают всю ширину стороны или превышают её. Увеличьте ширину, уточните положение дна или разрешите перераспределение.");
            double step = remainder / empty.Count;
            foreach (int index in empty) points[index].DistanceFromPrevious = step;
        }

        private static void ResolveOffsets(CrossSection source)
        {
            double previous = source.Points[0].Offset;
            ProfileValidation.Range(previous, -1000000000, 1000000000, "Начало поперечника");
            for (int i = 1; i < source.Points.Count; i++)
            {
                double next = source.Points[i].Offset;
                if (source.UsePointDistances)
                {
                    var interval = source.Points[i].DistanceFromPrevious;
                    if (!interval.HasValue || !NumberParser.Finite(interval.Value) || interval <= 0)
                        throw new ProfileValidationException("Сначала заполните расстояния: нажмите «Ширина между берегами…» или введите положительные интервалы вручную.");
                    next = previous + interval.Value;
                }
                ProfileValidation.Range(next, -1000000000, 1000000000, "Смещение поперечника");
                if (next <= previous) throw new ProfileValidationException("Расстояния слишком малы или точки расположены в неверном порядке.");
                source.Points[i].Offset = next;
                previous = next;
            }
        }

        // Shape-preserving cubic Hermite interpolation (PCHIP): monotone anchor
        // intervals stay monotone, without inventing local peaks or depressions.
        private static double[] Slopes(double[] x, double[] y)
        {
            int n = x.Length;
            var h = new double[n - 1]; var d = new double[n - 1]; var m = new double[n];
            for (int i = 0; i < n - 1; i++) { h[i] = x[i + 1] - x[i]; d[i] = (y[i + 1] - y[i]) / h[i]; }
            m[0] = Endpoint(h[0], h[1], d[0], d[1]);
            m[n - 1] = Endpoint(h[n - 2], h[n - 3], d[n - 2], d[n - 3]);
            for (int i = 1; i < n - 1; i++)
            {
                if (d[i - 1] == 0 || d[i] == 0 || Math.Sign(d[i - 1]) != Math.Sign(d[i])) m[i] = 0;
                else
                {
                    double w1 = 2 * h[i] + h[i - 1], w2 = h[i] + 2 * h[i - 1];
                    m[i] = (w1 + w2) / (w1 / d[i - 1] + w2 / d[i]);
                }
            }
            return m;
        }

        private static double Endpoint(double h0, double h1, double d0, double d1)
        {
            double value = ((2 * h0 + h1) * d0 - h0 * d1) / (h0 + h1);
            if (Math.Sign(value) != Math.Sign(d0)) return 0;
            return Math.Sign(d0) != Math.Sign(d1) && Math.Abs(value) > Math.Abs(3 * d0) ? 3 * d0 : value;
        }
    }
}
