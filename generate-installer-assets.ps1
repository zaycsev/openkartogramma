# ============================================================
#  Генерация графики для установщика (Inno Setup)
#  Выходные файлы:  dist\assets\wizard_side.png  (164x314)
#                   dist\assets\wizard_small.png  (55x55)
#                   dist\assets\setup.ico          (16/32/48 px)
# ============================================================
param([string]$Version = "1.1.1")

$ErrorActionPreference = "Stop"
$outDir = "$PSScriptRoot\dist\assets"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

Add-Type -AssemblyName System.Drawing

$drawingRef = [System.Drawing.Bitmap].Assembly.Location

Add-Type -ReferencedAssemblies $drawingRef -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;

public static class InstallerAssets
{
    // Вспомогательный метод вместо локальной функции (C# 5 compatible)
    private static PointF Pt(float c, float r,
        float ox, float oy, float dcx, float dcy, float drx, float dry)
    {
        return new PointF(ox + c * dcx + r * drx, oy + c * dcy + r * dry);
    }

    // Та же логика рисования что в AppInit.cs MakeIcon
    static void DrawKartogramma(Graphics g, float shiftX, float shiftY, float s)
    {
        const int COLS = 5, ROWS = 4;
        float dcx = 3.4f * s,  dcy = 1.5f * s;
        float drx = -2.8f * s, dry = 1.5f * s;
        float ox = shiftX + 13.5f * s;
        float oy = shiftY + 9.0f  * s;

        float[,] v = new float[ROWS, COLS] {
            { -0.85f, -0.70f, -0.45f, -0.15f,  0.20f },
            { -0.65f, -0.45f, -0.15f,  0.25f,  0.55f },
            { -0.30f, -0.05f,  0.25f,  0.55f,  0.75f },
            {  0.10f,  0.35f,  0.60f,  0.75f,  0.85f }
        };

        // Заливка ячеек
        for (int r = 0; r < ROWS; r++)
        for (int c = 0; c < COLS; c++)
        {
            float val = v[r, c];
            Color col;
            if (val < 0) {
                float t = -val;
                col = Color.FromArgb(180,
                    (int)(185 + 70 * t),
                    (int)(55  + 20 * (1 - t)),
                    (int)(55  + 20 * (1 - t)));
            } else {
                float t = val;
                col = Color.FromArgb(180,
                    (int)(50  + 25 * (1 - t)),
                    (int)(100 + 30 * (1 - t)),
                    (int)(185 + 70 * t));
            }
            SolidBrush br = new SolidBrush(col);
            g.FillPolygon(br, new PointF[] {
                Pt(c,   r,   ox, oy, dcx, dcy, drx, dry),
                Pt(c+1, r,   ox, oy, dcx, dcy, drx, dry),
                Pt(c+1, r+1, ox, oy, dcx, dcy, drx, dry),
                Pt(c,   r+1, ox, oy, dcx, dcy, drx, dry)
            });
            br.Dispose();
        }

        // Внутренняя сетка
        float lw = Math.Max(0.5f, 0.55f * s);
        Pen pi = new Pen(Color.FromArgb(160, 255, 255, 255), lw);
        for (int c = 1; c < COLS; c++)
            g.DrawLine(pi,
                Pt(c, 0,    ox, oy, dcx, dcy, drx, dry),
                Pt(c, ROWS, ox, oy, dcx, dcy, drx, dry));
        for (int r = 1; r < ROWS; r++)
            g.DrawLine(pi,
                Pt(0,    r, ox, oy, dcx, dcy, drx, dry),
                Pt(COLS, r, ox, oy, dcx, dcy, drx, dry));
        pi.Dispose();

        // Внешняя рамка (ярче)
        Pen pb = new Pen(Color.FromArgb(230, 255, 255, 255), lw * 1.6f);
        g.DrawPolygon(pb, new PointF[] {
            Pt(0,    0,    ox, oy, dcx, dcy, drx, dry),
            Pt(COLS, 0,    ox, oy, dcx, dcy, drx, dry),
            Pt(COLS, ROWS, ox, oy, dcx, dcy, drx, dry),
            Pt(0,    ROWS, ox, oy, dcx, dcy, drx, dry)
        });
        pb.Dispose();
    }

    // Квадратная иконка (для .ico, прозрачный фон)
    public static Bitmap MakeIcon(int size)
    {
        Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        Graphics g = Graphics.FromImage(bmp);
        g.SmoothingMode   = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);
        DrawKartogramma(g, 0f, 0f, size / 32f);
        g.Dispose();
        return bmp;
    }

    // Боковое изображение мастера (164x314)
    public static Bitmap MakeWizardSide(int w, int h, string version)
    {
        Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        Graphics g = Graphics.FromImage(bmp);
        g.SmoothingMode     = SmoothingMode.AntiAlias;
        g.PixelOffsetMode   = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;

        // Темный градиентный фон (Civil 3D Dark)
        LinearGradientBrush bg = new LinearGradientBrush(
            new Rectangle(0, 0, w, h),
            Color.FromArgb(255, 30, 38, 56),
            Color.FromArgb(255, 14, 20, 32),
            LinearGradientMode.Vertical);
        g.FillRectangle(bg, 0, 0, w, h);
        bg.Dispose();

        // Акцентная полоска справа
        Pen acPen = new Pen(Color.FromArgb(70, 0, 126, 198), 2f);
        g.DrawLine(acPen, w - 1, 0, w - 1, h);
        acPen.Dispose();

        // Картограмма: scale=4.3, центрирована в верхней части
        // gridX=11.5, gridY=-22.7 => topmost ~y=16, center x~82
        DrawKartogramma(g, 11.5f, -22.7f, 4.3f);

        // Разделитель под сеткой
        int divY = 88;
        Pen divPen = new Pen(Color.FromArgb(55, 0, 126, 198), 1f);
        g.DrawLine(divPen, 16, divY, w - 16, divY);
        divPen.Dispose();

        // Текст
        StringFormat fmt = new StringFormat();
        fmt.Alignment = StringAlignment.Center;

        Font fTitle = new Font("Segoe UI", 12f, FontStyle.Bold,    GraphicsUnit.Point);
        Font fSub   = new Font("Segoe UI",  9f, FontStyle.Regular, GraphicsUnit.Point);
        Font fVer   = new Font("Segoe UI",  7f, FontStyle.Regular, GraphicsUnit.Point);

        SolidBrush bTitle = new SolidBrush(Color.FromArgb(235, 235, 235));
        SolidBrush bSub   = new SolidBrush(Color.FromArgb(150, 185, 215));
        SolidBrush bVer   = new SolidBrush(Color.FromArgb(90,  110, 135));

        // Cyrillic title/subtitle. Written with \u escapes so the source stays
        // pure ASCII and the result does not depend on the .ps1 encoding when
        // compiled via Add-Type.
        // "Kartogramma" (Cyrillic)
        g.DrawString("\u041A\u0430\u0440\u0442\u043E\u0433\u0440\u0430\u043C\u043C\u0430",
            fTitle, bTitle, new RectangleF(0, divY + 14, w, 26), fmt);
        // "zemlyanyh rabot" (Cyrillic)
        g.DrawString("\u0437\u0435\u043C\u043B\u044F\u043D\u044B\u0445 \u0440\u0430\u0431\u043E\u0442",
            fSub,   bSub,   new RectangleF(0, divY + 40, w, 20), fmt);
        g.DrawString("v" + version,     fVer,   bVer,   new RectangleF(0, h - 20,    w, 16), fmt);

        fTitle.Dispose(); fSub.Dispose(); fVer.Dispose();
        bTitle.Dispose(); bSub.Dispose(); bVer.Dispose();
        fmt.Dispose();
        g.Dispose();
        return bmp;
    }

    // Маленькое изображение мастера (55x55)
    public static Bitmap MakeWizardSmall(int size)
    {
        Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        Graphics g = Graphics.FromImage(bmp);
        g.SmoothingMode   = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.FromArgb(255, 30, 38, 56));
        float sc = size / 32f * 0.86f;
        DrawKartogramma(g, size * 0.07f, size * 0.07f, sc);
        g.Dispose();
        return bmp;
    }

    // Multi-size ICO (PNG-in-ICO, Vista+)
    public static byte[] CreateIco(int[] sizes)
    {
        byte[][] pngs = new byte[sizes.Length][];
        for (int i = 0; i < sizes.Length; i++)
        {
            Bitmap bmp = MakeIcon(sizes[i]);
            MemoryStream ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            pngs[i] = ms.ToArray();
            bmp.Dispose();
            ms.Dispose();
        }

        MemoryStream ico = new MemoryStream();
        ico.Write(new byte[]{ 0, 0, 1, 0 }, 0, 4);
        ico.Write(BitConverter.GetBytes((short)sizes.Length), 0, 2);

        int dataOffset = 6 + sizes.Length * 16;
        for (int i = 0; i < sizes.Length; i++)
        {
            int sz = sizes[i];
            ico.WriteByte((byte)(sz >= 256 ? 0 : sz));
            ico.WriteByte((byte)(sz >= 256 ? 0 : sz));
            ico.WriteByte(0); ico.WriteByte(0);
            ico.Write(BitConverter.GetBytes((short)1),  0, 2);
            ico.Write(BitConverter.GetBytes((short)32), 0, 2);
            ico.Write(BitConverter.GetBytes(pngs[i].Length), 0, 4);
            ico.Write(BitConverter.GetBytes(dataOffset),     0, 4);
            dataOffset += pngs[i].Length;
        }
        foreach (byte[] png in pngs)
            ico.Write(png, 0, png.Length);

        byte[] result = ico.ToArray();
        ico.Dispose();
        return result;
    }
}
'@

# ── Генерация файлов ──────────────────────────────────────────────────────────
function Ok { Write-Host "  OK: $args" -ForegroundColor Green }

$side = [InstallerAssets]::MakeWizardSide(164, 314, $Version)
$side.Save("$outDir\wizard_side.png", [System.Drawing.Imaging.ImageFormat]::Png)
Ok "wizard_side.png   (164x314)"
$side.Dispose()

$small = [InstallerAssets]::MakeWizardSmall(55)
$small.Save("$outDir\wizard_small.png", [System.Drawing.Imaging.ImageFormat]::Png)
Ok "wizard_small.png  (55x55)"
$small.Dispose()

$icoBytes = [InstallerAssets]::CreateIco([int[]]@(16, 32, 48))
[System.IO.File]::WriteAllBytes("$outDir\setup.ico", $icoBytes)
Ok "setup.ico         (16 / 32 / 48 px)"
