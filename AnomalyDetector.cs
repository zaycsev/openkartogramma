using System;
using System.Collections.Generic;

namespace KartogrammaPlugin
{
    /// <summary>
    /// Поиск «резких перепадов» (зумпф, приямок, локальная насыпь) по полю
    /// рабочих отметок h[i,j] на равномерной сетке (NaN — нет данных).
    ///
    /// Критерий: связная КОМПАКТНАЯ область, где |h − медиана| превышает
    /// порог dev = max(minDev, 3·MAD), и пиковое отклонение области
    /// ≥ peakFactor·dev. Пиковый порог отсекает ложные срабатывания на
    /// кромках откосов (там отклонение едва выходит за порог), а лимит
    /// размера bbox — протяжённые «зоны» вдоль всего борта (это рельеф,
    /// а не локальный перепад). Возвращает bbox зон в индексах сетки
    /// (i0..i1, j0..j1 включительно), отсортированные по силе перепада.
    /// </summary>
    internal static class AnomalyDetector
    {
        internal static List<(int i0, int j0, int i1, int j1)> FindZones(
            double[,] h,
            double minDev     = 1.0,   // м: минимальный «резкий» перепад
            int    minCount   = 4,     // минимум сэмплов в зоне (защита от шума)
            int    maxSide    = 0,     // макс. сторона bbox в сэмплах (0 — без лимита)
            int    maxZones   = 10,
            double peakFactor = 2.5)
        {
            var result = new List<(int, int, int, int)>();
            int ni = h.GetLength(0), nj = h.GetLength(1);

            // ── Медиана и MAD по валидным сэмплам ────────────────────────────
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

            // ── Маска кандидатов и связные компоненты (BFS, 4-связность) ────
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

                    // 4 соседа
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
    }
}
