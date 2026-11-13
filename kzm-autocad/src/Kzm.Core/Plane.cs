using System;

namespace Kzm.Core
{
    /// <summary>
    /// Проектная плоскость (задание п. 2.3): Z = Z0 + Gx·(x − X0) + Gy·(y − Y0).
    /// Задаётся по трём точкам или по точке и двум уклонам (‰): вдоль направления и поперёк
    /// (поперёк — влево от направления, на 90° против часовой стрелки). Уклон «+» — подъём.
    /// </summary>
    public sealed class PlaneDefinition
    {
        private PlaneDefinition(XY origin, double z0, double gx, double gy)
        {
            Origin = origin;
            Z0 = z0;
            Gx = gx;
            Gy = gy;
        }

        public XY Origin { get; }
        public double Z0 { get; }
        /// <summary>dZ/dx, dZ/dy (м/м).</summary>
        public double Gx { get; }
        public double Gy { get; }

        public double ZAt(XY p) => Z0 + Gx * (p.X - Origin.X) + Gy * (p.Y - Origin.Y);

        /// <summary>Наибольший уклон, ‰.</summary>
        public double MaxSlopePermille => Math.Sqrt(Gx * Gx + Gy * Gy) * 1000;

        /// <summary>Направление подъёма по наибольшему уклону, градусы от оси X против часовой стрелки (0…360).</summary>
        public double RiseDirectionDegrees
        {
            get
            {
                if (Gx == 0 && Gy == 0) return 0;
                double degrees = Math.Atan2(Gy, Gx) * 180 / Math.PI;
                return degrees < 0 ? degrees + 360 : degrees;
            }
        }

        /// <summary>Уклон вдоль направления (градусы), ‰.</summary>
        public double SlopeAlongPermille(double directionDegrees)
        {
            double a = directionDegrees * Math.PI / 180;
            return (Gx * Math.Cos(a) + Gy * Math.Sin(a)) * 1000;
        }

        public static PlaneDefinition FromThreePoints(XY a, double za, XY b, double zb, XY c, double zc)
        {
            foreach (double value in new[] { a.X, a.Y, za, b.X, b.Y, zb, c.X, c.Y, zc })
                if (!Guard.Finite(value)) throw new KzmException("Координаты и отметки точек плоскости должны быть числами.");
            // Local coordinates: survey coordinates (~5 000 000 m) would lose precision in the determinant.
            double bx = b.X - a.X, by = b.Y - a.Y, cx = c.X - a.X, cy = c.Y - a.Y;
            double det = bx * cy - by * cx;
            double scale = Math.Max(1e-9, Math.Max(bx * bx + by * by, cx * cx + cy * cy));
            if (Math.Abs(det) <= 1e-9 * scale)
                throw new KzmException("Три точки плоскости лежат на одной прямой (или совпадают): плоскость не определена.");
            double dzb = zb - za, dzc = zc - za;
            double gx = (dzb * cy - dzc * by) / det;
            double gy = (bx * dzc - cx * dzb) / det;
            return new PlaneDefinition(a, za, gx, gy);
        }

        /// <param name="directionDegrees">Направление уклона 1, градусы от оси X против часовой стрелки.</param>
        /// <param name="alongPermille">Уклон вдоль направления, ‰ (+ подъём).</param>
        /// <param name="acrossPermille">Уклон поперёк (влево от направления), ‰ (+ подъём).</param>
        public static PlaneDefinition FromSlopes(XY origin, double z0, double directionDegrees, double alongPermille, double acrossPermille)
        {
            foreach (double value in new[] { origin.X, origin.Y, z0, directionDegrees, alongPermille, acrossPermille })
                if (!Guard.Finite(value)) throw new KzmException("Параметры плоскости должны быть числами.");
            if (Math.Abs(alongPermille) > 1000 || Math.Abs(acrossPermille) > 1000)
                throw new KzmException("Уклон больше 1000 ‰ (45°): проверьте единицы — уклоны вводятся в промилле.");
            double a = directionDegrees * Math.PI / 180;
            double ux = Math.Cos(a), uy = Math.Sin(a);
            double i1 = alongPermille / 1000, i2 = acrossPermille / 1000;
            // G·u = i1, G·n = i2, n = (−uy, ux).
            return new PlaneDefinition(origin, z0, i1 * ux - i2 * uy, i1 * uy + i2 * ux);
        }

        public string Describe(int decimals)
        {
            return "Z = " + ElevationFormat.Format(Z0, decimals) + " в точке " + Origin +
                   "; наибольший уклон " + ElevationFormat.Format(MaxSlopePermille, 2) + " ‰" +
                   (MaxSlopePermille > 0 ? ", подъём в направлении " + ElevationFormat.Format(RiseDirectionDegrees, 1) + "°" : string.Empty);
        }
    }
}
