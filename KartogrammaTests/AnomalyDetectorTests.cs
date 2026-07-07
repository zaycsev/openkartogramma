using System;
using System.Collections.Generic;
using Xunit;

namespace KartogrammaTests
{
    // ═══════════════════════════════════════════════════════════════
    //  Детектор резких перепадов (KartogrammaPlugin.AnomalyDetector):
    //  зумпф/приямок должен выделяться отдельной зоной, а откосы,
    //  кромки и общий рельеф — нет.
    //
    //  Сценарий пользователя: траншея (полоса, дно h≈−1.7 м) с ямой
    //  разработки под зумпф в углу (h≈−7 м). Прежде объём зумпфа
    //  размазывался по обычным ячейкам; теперь зона обнаруживается
    //  и считается отдельной ячейкой.
    // ═══════════════════════════════════════════════════════════════
    public class AnomalyDetectorTests
    {
        // Копия KartogrammaPlugin.AnomalyDetector.FindZones (без AutoCAD-зависимостей)
        private static List<(int i0, int j0, int i1, int j1)> FindZones(
            double[,] h, double minDev = 1.0, int minCount = 4,
            int maxSide = 0, int maxZones = 10, double peakFactor = 2.5)
        {
            var result = new List<(int, int, int, int)>();
            int ni = h.GetLength(0), nj = h.GetLength(1);

            var vals = new List<double>(ni * nj);
            for (int i = 0; i < ni; i++)
            for (int j = 0; j < nj; j++)
                if (!double.IsNaN(h[i, j])) vals.Add(h[i, j]);
            if (vals.Count < 16) return result;

            vals.Sort();
            double med = vals[vals.Count / 2];

            var devs = new List<double>(vals.Count);
            foreach (var v in vals) devs.Add(Math.Abs(v - med));
            devs.Sort();
            double mad = devs[devs.Count / 2];

            double dev = Math.Max(minDev, 3.0 * mad);

            var visited = new bool[ni, nj];
            bool Candidate(int i, int j) =>
                !double.IsNaN(h[i, j]) && Math.Abs(h[i, j] - med) > dev;

            var comps = new List<(int i0, int j0, int i1, int j1, double peak)>();
            var queue = new Queue<(int i, int j)>();

            for (int si = 0; si < ni; si++)
            for (int sj = 0; sj < nj; sj++)
            {
                if (visited[si, sj] || !Candidate(si, sj)) continue;

                int i0 = si, i1 = si, j0 = sj, j1 = sj, count = 0;
                double peak = 0;
                visited[si, sj] = true;
                queue.Enqueue((si, sj));
                while (queue.Count > 0)
                {
                    var (ci, cj) = queue.Dequeue();
                    count++;
                    if (ci < i0) i0 = ci; if (ci > i1) i1 = ci;
                    if (cj < j0) j0 = cj; if (cj > j1) j1 = cj;
                    double d = Math.Abs(h[ci, cj] - med);
                    if (d > peak) peak = d;

                    if (ci > 0      && !visited[ci - 1, cj] && Candidate(ci - 1, cj)) { visited[ci - 1, cj] = true; queue.Enqueue((ci - 1, cj)); }
                    if (ci < ni - 1 && !visited[ci + 1, cj] && Candidate(ci + 1, cj)) { visited[ci + 1, cj] = true; queue.Enqueue((ci + 1, cj)); }
                    if (cj > 0      && !visited[ci, cj - 1] && Candidate(ci, cj - 1)) { visited[ci, cj - 1] = true; queue.Enqueue((ci, cj - 1)); }
                    if (cj < nj - 1 && !visited[ci, cj + 1] && Candidate(ci, cj + 1)) { visited[ci, cj + 1] = true; queue.Enqueue((ci, cj + 1)); }
                }

                if (count < minCount) continue;
                if (maxSide > 0 && (i1 - i0 + 1 > maxSide || j1 - j0 + 1 > maxSide)) continue;
                if (peak < peakFactor * dev) continue;

                comps.Add((i0, j0, i1, j1, peak));
            }

            comps.Sort((a, b) => b.peak.CompareTo(a.peak));
            for (int k = 0; k < comps.Count && k < maxZones; k++)
                result.Add((comps[k].i0, comps[k].j0, comps[k].i1, comps[k].j1));
            return result;
        }

        // Синтетическая траншея-рамка 100×80 сэмплов: полоса шириной 12 сэмплов
        // по периметру, дно −1.7 м, наружные 2 сэмпла полосы — «откос» (h → 0).
        // Вне полосы (снаружи и в «острове») — NaN.
        private static double[,] TrenchField(bool withSump)
        {
            const int NI = 80, NJ = 100, BAND = 12;
            var h = new double[NI, NJ];
            for (int i = 0; i < NI; i++)
            for (int j = 0; j < NJ; j++)
            {
                int edge = Math.Min(Math.Min(i, NI - 1 - i), Math.Min(j, NJ - 1 - j));
                if (edge >= BAND) { h[i, j] = double.NaN; continue; }   // остров
                // откос на наружных двух сэмплах: −0.3 и −0.9, дальше дно −1.7
                h[i, j] = edge == 0 ? -0.3 : edge == 1 ? -0.9 : -1.7;
            }

            if (withSump)
                for (int i = 3; i < 9; i++)      // зумпф 6×6 сэмплов в углу полосы
                for (int j = 3; j < 9; j++)
                    h[i, j] = -7.0;
            return h;
        }

        [Fact]
        public void TrenchWithSump_SumpDetected_OnceAndInPlace()
        {
            var zones = FindZones(TrenchField(withSump: true), maxSide: 20);

            Assert.Single(zones);
            var z = zones[0];
            // Зона накрывает зумпф (3..8 × 3..8) и не расползается на всю полосу
            Assert.True(z.i0 >= 2 && z.i1 <= 10, $"i-диапазон {z.i0}..{z.i1}");
            Assert.True(z.j0 >= 2 && z.j1 <= 10, $"j-диапазон {z.j0}..{z.j1}");
        }

        [Fact]
        public void TrenchWithoutSump_NothingDetected()
        {
            // Откосы и кромки полосы — не «резкий перепад»
            var zones = FindZones(TrenchField(withSump: false), maxSide: 20);
            Assert.Empty(zones);
        }

        [Fact]
        public void ElongatedTerrain_RejectedByMaxSide()
        {
            // Глубокая, но протяжённая полоса (рельеф) — bbox больше лимита
            var h = TrenchField(withSump: false);
            for (int j = 2; j < 98; j++)         // «канава» вдоль всего низа
                for (int i = 4; i < 8; i++)
                    h[i, j] = -7.0;
            var zones = FindZones(h, maxSide: 20);
            Assert.Empty(zones);
        }

        [Fact]
        public void LocalFillBump_DetectedToo()
        {
            // Локальная НАСЫПЬ (+5 при фоне −1.7) — перепад в другую сторону
            var h = TrenchField(withSump: false);
            for (int i = 3; i < 9; i++)
            for (int j = 40; j < 46; j++)
                h[i, j] = +5.0;
            var zones = FindZones(h, maxSide: 20);
            Assert.Single(zones);
        }

        [Fact]
        public void NoisyShallowSpeck_IgnoredByMinCount()
        {
            // Пара шумовых сэмплов — меньше minCount, не зона
            var h = TrenchField(withSump: false);
            h[5, 50] = -7.0;
            h[5, 51] = -7.0;
            var zones = FindZones(h, maxSide: 20);
            Assert.Empty(zones);
        }

        [Fact]
        public void TwoSumps_BothDetected_StrongestFirst()
        {
            var h = TrenchField(withSump: true);          // зумпф −7.0 в углу
            for (int i = 3; i < 9; i++)                    // второй, поглубже
            for (int j = 60; j < 66; j++)
                h[i, j] = -9.0;
            var zones = FindZones(h, maxSide: 20);
            Assert.Equal(2, zones.Count);
            Assert.True(zones[0].j0 >= 55, "первым идёт более сильный перепад (−9)");
        }
    }
}
