using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace KartogrammaPlugin
{
    /// <summary>
    /// Диалог настройки имён слоёв картограммы.
    /// Открывается из главного окна по клику на шестерёнку.
    /// Размер совпадает с главным окном; «Закрыть»/«ОК» — справа снизу
    /// в той же позиции, что и в главном диалоге.
    /// </summary>
    public sealed class LayerSettingsForm : Form
    {
        private readonly TextBox _txtGrid   = new();
        private readonly TextBox _txtExist  = new();
        private readonly TextBox _txtDesign = new();
        private readonly TextBox _txtWork   = new();
        private readonly TextBox _txtVolume = new();
        private readonly TextBox _txtTable  = new();
        private readonly TextBox _txtText   = new();
        private readonly TextBox _txtHatch  = new();
        private readonly TextBox _txtZero   = new();

        public string GridLayer   => _txtGrid.Text.Trim();
        public string ExistLayer  => _txtExist.Text.Trim();
        public string DesignLayer => _txtDesign.Text.Trim();
        public string WorkLayer   => _txtWork.Text.Trim();
        public string VolumeLayer => _txtVolume.Text.Trim();
        public string TableLayer  => _txtTable.Text.Trim();
        public string TextLayer   => _txtText.Text.Trim();
        public string HatchLayer  => _txtHatch.Text.Trim();
        public string ZeroLayer   => _txtZero.Text.Trim();

        public LayerSettingsForm(
            string grid, string exist, string design,
            string work, string volume, string table, string text,
            string hatch, string zero,
            Size mainSize)
        {
            Text            = "Картограмма — настройка слоёв";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition   = FormStartPosition.CenterParent;
            MaximizeBox     = false;
            MinimizeBox     = false;
            AutoScaleMode   = AutoScaleMode.None;   // масштабируем вручную, как главное окно
            ClientSize      = mainSize;
            Font            = new Font("Segoe UI", 9f);

            _txtGrid.Text   = grid;
            _txtExist.Text  = exist;
            _txtDesign.Text = design;
            _txtWork.Text   = work;
            _txtVolume.Text = volume;
            _txtTable.Text  = table;
            _txtText.Text   = text;
            _txtHatch.Text  = hatch;
            _txtZero.Text   = zero;

            // Шрифт задан в пунктах и растёт вместе с DPI — значит и пиксельные
            // размеры обязаны расти, иначе подписи вылезают за свои рамки.
            float dpiScale;
            using (var g = CreateGraphics()) { dpiScale = g.DpiX / 96f; }
            int S(int v) => (int)Math.Round(v * dpiScale);

            var body = new Font("Segoe UI", 9f);

            // Без NoPadding: меряем ровно так, как Label потом нарисует, иначе
            // замер выходит уже отрисовки и последнюю букву срезает.
            Size TextSize(string text, Font f)
                => TextRenderer.MeasureText(text, f, new Size(int.MaxValue, int.MaxValue),
                                            TextFormatFlags.SingleLine);

            // ── Группа со списком слоёв ─────────────────────────────────────
            var grp = new GroupBox
            {
                Text     = "─< Имена слоёв картограммы >─",
                Location = new Point(S(8), S(8)),
                Size     = new Size(mainSize.Width - S(16), S(292)),  // высоту уточним по строкам
                Font     = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            Controls.Add(grp);

            var rows = new (string Label, TextBox Box)[]
            {
                ("Сетка квадратов:",            _txtGrid),
                ("Чёрные отметки (до):",        _txtExist),
                ("Красные отметки (после):",    _txtDesign),
                ("Рабочие отметки (разница):",  _txtWork),
                ("Объёмы по ячейкам:",          _txtVolume),
                ("Итоговая таблица и подпись:", _txtTable),
                ("Прочие текстовые подписи:",   _txtText),
                ("Штриховка выемки и насыпи:",  _txtHatch),
                ("Линия нулевых работ:",        _txtZero)
            };

            // Колонка подписей — по самой длинной из них, а не «на глаз».
            int lblX = S(12), lblW = 0;
            foreach (var r in rows) lblW = Math.Max(lblW, TextSize(r.Label, body).Width);
            lblW += S(10);

            int boxX = lblX + lblW;
            int boxW = Math.Max(S(120), grp.ClientSize.Width - boxX - S(12));
            int lblH = TextSize("Ауj", body).Height + S(2);

            int yRow = TextSize("Ауj", grp.Font).Height + S(10);
            int rowH = 0;   // фактическую высоту строки узнаём по первому TextBox

            void Row(string label, TextBox tb)
            {
                tb.Font     = body;                 // высоту TextBox назначает себе сам по шрифту
                tb.Location = new Point(boxX, yRow);
                tb.Width    = boxW;
                grp.Controls.Add(tb);

                if (rowH == 0) rowH = tb.Height + S(7);

                grp.Controls.Add(new Label
                {
                    Text      = label,
                    Location  = new Point(lblX, yRow + Math.Max(0, (tb.Height - lblH) / 2)),
                    Size      = new Size(lblW, lblH),
                    Font      = body,
                    TextAlign = ContentAlignment.MiddleLeft
                });
                yRow += rowH;
            }

            foreach (var r in rows) Row(r.Label, r.Box);

            grp.Height = yRow + S(8);

            // ── Кнопки внизу: ОК + Закрыть (Закрыть в той же позиции,
            //    что в главном окне — правый нижний угол) ──────────────────────
            int closeW = Math.Max(S(100), TextSize("Закрыть", body).Width + S(28));
            int btnH   = Math.Max(S(28), TextSize("Ауj", body).Height + S(10));
            int btnY   = mainSize.Height - btnH - S(14);

            var btnOk = new Button
            {
                Text         = "ОК",
                Location     = new Point(S(8), btnY),
                Size         = new Size(closeW, btnH),
                DialogResult = DialogResult.OK
            };
            Controls.Add(btnOk);

            var btnClose = new Button
            {
                Text         = "Закрыть",
                Location     = new Point(mainSize.Width - closeW - S(8), btnY),
                Size         = new Size(closeW, btnH),
                DialogResult = DialogResult.Cancel
            };
            Controls.Add(btnClose);

            // ── Ссылка на страницу загрузки новых версий (GitHub Releases) ──────
            //    Профиль: https://github.com/zaycsev
            //    Репозиторий программы: openkartogramma
            const string releasesUrl = "https://github.com/zaycsev/openkartogramma/releases";

            int linkX = S(8) + closeW + S(8);
            int linkH = TextSize("Ауj", body).Height + S(4);
            var lnkUpdate = new LinkLabel
            {
                Text             = "Скачать новую версию (GitHub)",
                Location         = new Point(linkX, btnY + Math.Max(0, (btnH - linkH) / 2)),
                Size             = new Size(Math.Max(S(80), mainSize.Width - linkX - closeW - S(16)), linkH),
                TextAlign        = ContentAlignment.MiddleCenter,
                AutoEllipsis     = true,
                Font             = body,
                LinkBehavior     = LinkBehavior.HoverUnderline,
                // Цвет ссылки — как у обычного текста в программе
                LinkColor        = SystemColors.ControlText,
                ActiveLinkColor  = SystemColors.ControlText,
                VisitedLinkColor = SystemColors.ControlText
            };
            var tip = new ToolTip();
            tip.SetToolTip(lnkUpdate, releasesUrl);
            lnkUpdate.LinkClicked += (s, e) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName        = releasesUrl,
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        "Не удалось открыть ссылку в браузере:\n" + releasesUrl +
                        "\n\n" + ex.Message,
                        "Картограмма",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            Controls.Add(lnkUpdate);

            AcceptButton = btnOk;
            CancelButton = btnClose;
        }
    }
}
