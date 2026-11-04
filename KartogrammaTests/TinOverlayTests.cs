using System;
using System.Collections.Generic;
using System.Linq;
using KartogrammaPlugin;
using Xunit;

namespace KartogrammaTests
{
    // ═══════════════════════════════════════════════════════════════
    //  Точное разделение ячейки на насыпь и выемку по двум TIN
    //  (KartogrammaPlugin.TinOverlay). Контуры штриховки и нулевая
    //  линия строятся без выборки — проверяем, что они совпадают с
    //  истинным знаком рабочей отметки в любой точке, что площади
    //  контуров сходятся с площадями кусков, и точные объёмы.
    // ═══════════════════════════════════════════════════════════════
    public class TinOverlayTests
    {
        private const double S = 5.0;

        /// <summary>Случайная TIN: решётка шага g со сдвинутыми узлами,
        /// каждый четырёхугольник делится по случайной диагонали.</summary>
        private static List<TinOverlay.Tri> RandomTin(Random rnd, double g,
            Func<double, double, double> z)
        {
            double ox = -g * (1 + rnd.NextDouble()), oy = -g * (1 + rnd.NextDouble());
            int n = (int)Math.Ceiling((S - ox) / g) + 2;
            var px = new double[n + 1, n + 1];
            var py = new double[n + 1, n + 1];
            for (int i = 0; i <= n; i++)
            for (int j = 0; j <= n; j++)
            {
                px[i, j] = ox + j * g + (rnd.NextDouble() - 0.5) * g * 0.25;
                py[i, j] = oy + i * g + (rnd.NextDouble() - 0.5) * g * 0.25;
            }
            var tris = new List<TinOverlay.Tri>();
            TinOverlay.Tri T(int i1, int j1, int i2, int j2, int i3, int j3) => new(
                px[i1, j1], py[i1, j1], z(px[i1, j1], py[i1, j1]),
                px[i2, j2], py[i2, j2], z(px[i2, j2], py[i2, j2]),
                px[i3, j3], py[i3, j3], z(px[i3, j3], py[i3, j3]));
            for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                if (rnd.Next(2) == 0)
                {
                    tris.Add(T(i, j, i, j + 1, i + 1, j + 1));
                    tris.Add(T(i, j, i + 1, j + 1, i + 1, j));
                }
                else
                {
                    tris.Add(T(i, j, i, j + 1, i + 1, j));
                    tris.Add(T(i, j + 1, i + 1, j + 1, i + 1, j));
                }
            }
            return tris;
        }

        private static double? Elev(List<TinOverlay.Tri> tin, double x, double y)
        {
            foreach (var t in tin)
            {
                double d = (t.Y2 - t.Y3) * (t.X1 - t.X3) + (t.X3 - t.X2) * (t.Y1 - t.Y3);
                if (Math.Abs(d) < 1e-14) continue;
                double l1 = ((t.Y2 - t.Y3) * (x - t.X3) + (t.X3 - t.X2) * (y - t.Y3)) / d;
                double l2 = ((t.Y3 - t.Y1) * (x - t.X3) + (t.X1 - t.X3) * (y - t.Y3)) / d;
                double l3 = 1 - l1 - l2;
                if (l1 < -1e-12 || l2 < -1e-12 || l3 < -1e-12) continue;
                return l1 * t.Z1 + l2 * t.Z2 + l3 * t.Z3;
            }
            return null;
        }

        private static bool Inside(List<List<(double X, double Y)>> loops, double x, double y)
        {
            bool c = false;
            foreach (var l in loops)
                for (int a = 0, b = l.Count - 1; a < l.Count; b = a++)
                    if ((l[a].Y > y) != (l[b].Y > y) &&
                        x < (l[b].X - l[a].X) * (y - l[a].Y) / (l[b].Y - l[a].Y) + l[a].X)
                        c = !c;
            return c;
        }

        private static double LoopsArea(List<List<(double X, double Y)>> loops) =>
            loops.Sum(l => TinOverlay.Area(l));

        [Fact]
        public void RandomTins_ContoursMatchTrueSign()
        {
            var rnd = new Random(7);
            for (int trial = 0; trial < 60; trial++)
            {
                double cx = rnd.NextDouble() * S, cy = rnd.NextDouble() * S;
                double rad = 0.5 + rnd.NextDouble() * 3;
                double noise = rnd.NextDouble() * 0.3;
                var t1 = RandomTin(rnd, 0.3 + rnd.NextDouble(), (x, y) => (rnd.NextDouble() - 0.5) * noise);
                var t2 = RandomTin(rnd, 0.3 + rnd.NextDouble(), (x, y) =>
                    (rad - Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy))) * 0.3);

                var r = TinOverlay.Build(S, S, t1, t2);
                Assert.NotNull(r);

                // Площадь контуров (дырки — со знаком минус) = площадь кусков
                Assert.Equal(r!.FillArea, LoopsArea(r.Fill), 8);
                Assert.Equal(r.CutArea,  LoopsArea(r.Cut),  8);
                Assert.Equal(S * S, r.Area, 8);

                for (int k = 0; k < 1500; k++)
                {
                    double x = rnd.NextDouble() * S, y = rnd.NextDouble() * S;
                    double h = Elev(t2, x, y)!.Value - Elev(t1, x, y)!.Value;
                    if (Math.Abs(h) < 1e-9) continue;
                    Assert.True(Inside(r.Fill, x, y) == (h > 0), $"trial {trial}: ({x},{y}) h={h}");
                    Assert.True(Inside(r.Cut,  x, y) == (h < 0), $"trial {trial}: ({x},{y}) h={h}");
                }
            }
        }

        [Fact]
        public void FineTins_FastEnough()
        {
            // ~1300 треугольников каждой поверхности на ячейку 5×5 (шаг 0.15 м)
            var rnd = new Random(3);
            var t1 = RandomTin(rnd, 0.15, (x, y) => Math.Sin(x * 3) * 0.05);
            var t2 = RandomTin(rnd, 0.15, (x, y) => Math.Cos(y * 2.7) * 0.05);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            TinOverlay.Result? r = null;
            for (int k = 0; k < 5; k++) r = TinOverlay.Build(S, S, t1, t2);
            double ms = sw.Elapsed.TotalMilliseconds / 5;
            Assert.NotNull(r);
            Assert.Equal(S * S, r!.FillArea + r.CutArea, 6);
            Assert.True(ms < 400, $"ячейка считается {ms:F0} мс");
        }

        [Fact]
        public void Plane_ExactVolumesAndZeroLine()
        {
            // Z1 = 0, Z2 = x − 2.5 → нулевая линия x = 2.5
            var flat = new List<TinOverlay.Tri>
            {
                new(-1, -1, 0, 6, -1, 0, 6, 6, 0), new(-1, -1, 0, 6, 6, 0, -1, 6, 0),
            };
            var slope = new List<TinOverlay.Tri>
            {
                new(-1, -1, -3.5, 6, -1, 3.5, 6, 6, 3.5), new(-1, -1, -3.5, 6, 6, 3.5, -1, 6, -3.5),
            };
            var r = TinOverlay.Build(S, S, flat, slope)!;
            Assert.Equal(12.5, r.FillArea, 10);
            Assert.Equal(12.5, r.CutArea, 10);
            Assert.Equal(15.625, r.FillVolume, 10);
            Assert.Equal(-15.625, r.CutVolume, 10);
            Assert.Single(r.Zero);
            Assert.Equal(2, r.Zero[0].Count);
            foreach (var p in r.Zero[0]) Assert.Equal(2.5, p.X, 10);
        }

        [Fact]
        public void ThinRidge_OneMillimetre_IsNotLost()
        {
            // Гребень насыпи шириной ~1 мм вдоль x = 2.5 — между узлами любой
            // разумной выборки он проскакивает; точный метод обязан его найти.
            var flat = new List<TinOverlay.Tri>
            {
                new(-1, -1, 0, 6, -1, 0, 6, 6, 0), new(-1, -1, 0, 6, 6, 0, -1, 6, 0),
            };
            double top = 0.0005;
            var ridge = new List<TinOverlay.Tri>
            {
                new(2.0, -1, -1, 2.5, -1, top, 2.5, 6, top), new(2.0, -1, -1, 2.5, 6, top, 2.0, 6, -1),
                new(2.5, -1, top, 3.0, -1, -1, 3.0, 6, -1),  new(2.5, -1, top, 3.0, 6, -1, 2.5, 6, top),
                new(-1, -1, -1, 2.0, -1, -1, 2.0, 6, -1),    new(-1, -1, -1, 2.0, 6, -1, -1, 6, -1),
                new(3.0, -1, -1, 6, -1, -1, 6, 6, -1),       new(3.0, -1, -1, 6, 6, -1, 3.0, 6, -1),
            };
            var r = TinOverlay.Build(S, S, flat, ridge)!;
            double w = 2 * 0.5 * top / (1 + top);           // ширина гребня
            Assert.Equal(S * w, r.FillArea, 10);
            Assert.Single(r.Fill);
            Assert.Equal(S * w, LoopsArea(r.Fill), 10);
            Assert.Equal(2, r.Zero.Count);                   // две нулевые линии по краям
        }
    }
}
