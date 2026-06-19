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

        public string GridLayer   => _txtGrid.Text.Trim();
        public string ExistLayer  => _txtExist.Text.Trim();
        public string DesignLayer => _txtDesign.Text.Trim();
        public string WorkLayer   => _txtWork.Text.Trim();
        public string VolumeLayer => _txtVolume.Text.Trim();
        public string TableLayer  => _txtTable.Text.Trim();
        public string TextLayer   => _txtText.Text.Trim();

        public LayerSettingsForm(
            string grid, string exist, string design,
            string work, string volume, string table, string text,
            Size mainSize)
        {
            Text            = "Картограмма — настройка слоёв";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition   = FormStartPosition.CenterParent;
            MaximizeBox     = false;
            MinimizeBox     = false;
            ClientSize      = mainSize;
            Font            = new Font("Segoe UI", 9f);

            _txtGrid.Text   = grid;
            _txtExist.Text  = exist;
            _txtDesign.Text = design;
            _txtWork.Text   = work;
            _txtVolume.Text = volume;
            _txtTable.Text  = table;
            _txtText.Text   = text;

            int S(int v) => v;

            // ── Группа со списком слоёв ─────────────────────────────────────
            var grp = new GroupBox
            {
                Text     = "─< Имена слоёв картограммы >─",
                Location = new Point(S(8), S(8)),
                Size     = new Size(mainSize.Width - S(16), S(292)),
                Font     = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            Controls.Add(grp);

            int yRow = S(28);
            int rowH = S(32);
            int lblX = S(12), lblW = S(260);
            int boxX = S(280);
            int boxW = grp.ClientSize.Width - boxX - S(12);

            void Row(string label, TextBox tb)
            {
                grp.Controls.Add(new Label
                {
                    Text     = label,
                    Location = new Point(lblX, yRow + S(4)),
                    Size     = new Size(lblW, S(20)),
                    Font     = new Font("Segoe UI", 9f)
                });
                tb.Location = new Point(boxX, yRow);
                tb.Size     = new Size(boxW, S(22));
                tb.Font     = new Font("Segoe UI", 9f);
                grp.Controls.Add(tb);
                yRow += rowH;
            }

            Row("Сетка квадратов:",            _txtGrid);
            Row("Чёрные отметки (до):",        _txtExist);
            Row("Красные отметки (после):",    _txtDesign);
            Row("Рабочие отметки (разница):",  _txtWork);
            Row("Объёмы по ячейкам:",          _txtVolume);
            Row("Итоговая таблица и подпись:", _txtTable);
            Row("Прочие текстовые подписи:",   _txtText);

            // ── Кнопки внизу: ОК + Закрыть (Закрыть в той же позиции,
            //    что в главном окне — правый нижний угол) ──────────────────────
            int closeW = S(100);
            int btnY   = mainSize.Height - S(42);

            var btnOk = new Button
            {
                Text         = "ОК",
                Location     = new Point(S(8), btnY),
                Size         = new Size(closeW, S(28)),
                DialogResult = DialogResult.OK
            };
            Controls.Add(btnOk);

            var btnClose = new Button
            {
                Text         = "Закрыть",
                Location     = new Point(mainSize.Width - closeW - S(8), btnY),
                Size         = new Size(closeW, S(28)),
                DialogResult = DialogResult.Cancel
            };
            Controls.Add(btnClose);

            // ── Ссылка на страницу загрузки новых версий (GitHub Releases) ──────
            //    Профиль: https://github.com/zaycsev
            //    Репозиторий программы: openkartogramma
            const string releasesUrl = "https://github.com/zaycsev/openkartogramma/releases";

            int linkX = S(8) + closeW + S(8);
            var lnkUpdate = new LinkLabel
            {
                Text             = "Скачать новую версию (GitHub)",
                Location         = new Point(linkX, btnY + S(5)),
                Size             = new Size(mainSize.Width - linkX - closeW - S(16), S(20)),
                TextAlign        = ContentAlignment.MiddleCenter,
                Font             = new Font("Segoe UI", 9f),
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
