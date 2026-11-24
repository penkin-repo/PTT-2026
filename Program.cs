using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using WpfInt32Rect = System.Windows.Int32Rect;
using WpfFreezable = System.Windows.Freezable;
using WpfMedia = System.Windows.Media;
using WpfImaging = System.Windows.Media.Imaging;

namespace PdfToCmykTiff
{
    static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch { }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string[] pdfArgs = (args ?? new string[0])
                .Where(a => !string.IsNullOrWhiteSpace(a) && File.Exists(a) &&
                            a.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            Application.Run(new MainForm(pdfArgs));
        }
    }

    sealed class PageSizePt
    {
        public double WidthPt;
        public double HeightPt;
    }

    sealed class NativeDpiInfo
    {
        public int Dpi;
        public string SourceLabel;
    }

    sealed class PdfImageInfo
    {
        public int WidthPx;
        public int HeightPx;
        public bool IsMask;
    }

    struct CtmMatrix
    {
        public double A, B, C, D, E, F;

        public static CtmMatrix Identity()
        {
            return new CtmMatrix { A = 1, B = 0, C = 0, D = 1, E = 0, F = 0 };
        }

        public static CtmMatrix Multiply(CtmMatrix m, CtmMatrix ctm)
        {
            return new CtmMatrix
            {
                A = m.A * ctm.A + m.B * ctm.C,
                B = m.A * ctm.B + m.B * ctm.D,
                C = m.C * ctm.A + m.D * ctm.C,
                D = m.C * ctm.B + m.D * ctm.D,
                E = m.E * ctm.A + m.F * ctm.C + ctm.E,
                F = m.E * ctm.B + m.F * ctm.D + ctm.F
            };
        }
    }

    sealed class ProfileItem
    {
        public string Name;
        public string FullPath;
        public override string ToString() { return Name; }
    }

    sealed class MainForm : Form
    {
        private const double PtPerMm = 72.0 / 25.4;
        private const int DefaultVectorDpi = 300;

        private static readonly Regex RxObjStart = new Regex(@"^\s*(\d+)\s+\d+\s+obj\b", RegexOptions.Compiled);
        private static readonly Regex RxWidth = new Regex(@"/Width\s+(\d+)\b", RegexOptions.Compiled);
        private static readonly Regex RxHeight = new Regex(@"/Height\s+(\d+)\b", RegexOptions.Compiled);
        private static readonly Regex RxSMaskRef = new Regex(@"/SMask\s+(\d+)\s+\d+\s+R\b", RegexOptions.Compiled);
        private static readonly Regex RxUserUnit = new Regex(@"/UserUnit\s+([+-]?\d+(?:\.\d+)?)\b", RegexOptions.Compiled);
        private static readonly Regex RxNameRef = new Regex(@"/([A-Za-z0-9_.\-+]+)\s+(\d+)\s+\d+\s+R\b", RegexOptions.Compiled);
        private static readonly Regex RxCm = new Regex(@"([+-]?\d+(?:\.\d+)?)\s+([+-]?\d+(?:\.\d+)?)\s+([+-]?\d+(?:\.\d+)?)\s+([+-]?\d+(?:\.\d+)?)\s+([+-]?\d+(?:\.\d+)?)\s+([+-]?\d+(?:\.\d+)?)\s+cm\b", RegexOptions.Compiled);
        private static readonly Regex RxDo = new Regex(@"/([A-Za-z0-9_.\-+]+)\s+Do\b", RegexOptions.Compiled);
        private static readonly Regex RxVectorOp = new Regex(@"(?:^|\s)(?:S|s|f|F|f\*|B|B\*|b|b\*|Tj|TJ|BT|sh)(?:\s|$)", RegexOptions.Compiled);

        private readonly string _baseDir;
        private readonly string[] _startupFiles;
        private string _gsPath;

        private ComboBox _cbProfile;
        private Button _btnTopMost;
        private TextBox _tbTargetW;
        private TextBox _tbTargetH;
        private CheckBox _chkSplit;
        private CheckBox _chkOverprint;
        private CheckBox _chkRichBlack;
        private ToolTip _toolTip;
        private Panel _dropZone;
        private Label _lblDropMain;
        private Label _lblDropSub;
        private TextBox _tbLog;

        private bool _isDragOver;
        private bool _isBusy;
        private bool _autoCloseOnDone;
        private Process _activeProcess;
        private CancellationTokenSource _cts;

        public MainForm(string[] startupFiles)
        {
            _baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _startupFiles = startupFiles ?? new string[0];
            _autoCloseOnDone = _startupFiles.Length > 0;

            BuildUi();
            LoadResources();
        }

        private void BuildUi()
        {
            SuspendLayout();

            Text = "PDF \u2192 CMYK TIFF (LZW)";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            ClientSize = new Size(460, 444);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(28, 29, 34);
            ForeColor = Color.FromArgb(232, 234, 240);
            Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);

            _toolTip = new ToolTip();
            _toolTip.InitialDelay = 350;
            _toolTip.AutoPopDelay = 8000;

            Color inputBg = Color.FromArgb(42, 44, 52);
            Color mutedText = Color.FromArgb(160, 166, 180);

            int pad = 16;
            int y = 14;
            int labelW = 122;
            int fieldX = pad + labelW;
            int fieldW = ClientSize.Width - fieldX - pad;

            Label lblProfile = new Label();
            lblProfile.Text = "ICC-\u043f\u0440\u043e\u0444\u0438\u043b\u044c:";
            lblProfile.Location = new Point(pad, y + 4);
            lblProfile.Size = new Size(labelW, 22);
            lblProfile.ForeColor = mutedText;
            Controls.Add(lblProfile);

            int topBtnW = 96;
            int topBtnGap = 8;

            _cbProfile = new ComboBox();
            _cbProfile.DropDownStyle = ComboBoxStyle.DropDownList;
            _cbProfile.Location = new Point(fieldX, y);
            _cbProfile.Size = new Size(fieldW - topBtnW - topBtnGap, 26);
            _cbProfile.BackColor = inputBg;
            _cbProfile.ForeColor = ForeColor;
            _cbProfile.FlatStyle = FlatStyle.Flat;
            _cbProfile.TabIndex = 2;
            Controls.Add(_cbProfile);

            _btnTopMost = new Button();
            _btnTopMost.Location = new Point(ClientSize.Width - pad - topBtnW, y);
            _btnTopMost.Size = new Size(topBtnW, 25);
            _btnTopMost.FlatStyle = FlatStyle.Flat;
            _btnTopMost.FlatAppearance.BorderSize = 1;
            _btnTopMost.Font = new Font("Segoe UI", 8.25f, FontStyle.Regular);
            _btnTopMost.Cursor = Cursors.Hand;
            _btnTopMost.TabStop = false;
            _btnTopMost.Click += (s, e) =>
            {
                TopMost = !TopMost;
                UpdateTopMostButton();
            };
            UpdateTopMostButton();
            _toolTip.SetToolTip(_btnTopMost, "\u0417\u0430\u043a\u0440\u0435\u043f\u0438\u0442\u044c \u043e\u043a\u043d\u043e \u043f\u043e\u0432\u0435\u0440\u0445 \u0432\u0441\u0435\u0445 \u043e\u043a\u043e\u043d \u0434\u043b\u044f \u0443\u0434\u043e\u0431\u043d\u043e\u0433\u043e \u043f\u0435\u0440\u0435\u0442\u0430\u0441\u043a\u0438\u0432\u0430\u043d\u0438\u044f PDF");
            Controls.Add(_btnTopMost);

            y += 34;

            Label lblCrop = new Label();
            lblCrop.Text = "\u041e\u0431\u0440\u0435\u0437\u0430\u0442\u044c \u0434\u043e (\u043c\u043c):";
            lblCrop.Location = new Point(pad, y + 4);
            lblCrop.Size = new Size(labelW, 22);
            lblCrop.ForeColor = mutedText;
            Controls.Add(lblCrop);

            Label lblW = new Label();
            lblW.Text = "\u0428:";
            lblW.Location = new Point(fieldX, y + 4);
            lblW.Size = new Size(20, 22);
            lblW.ForeColor = mutedText;
            Controls.Add(lblW);

            _tbTargetW = new TextBox();
            _tbTargetW.Text = "";
            _tbTargetW.Location = new Point(fieldX + 22, y + 1);
            _tbTargetW.Size = new Size(118, 25);
            _tbTargetW.BackColor = inputBg;
            _tbTargetW.ForeColor = ForeColor;
            _tbTargetW.BorderStyle = BorderStyle.FixedSingle;
            _tbTargetW.TextAlign = HorizontalAlignment.Center;
            _tbTargetW.TabIndex = 0;
            AttachAutoSelect(_tbTargetW);
            Controls.Add(_tbTargetW);

            Label lblX = new Label();
            lblX.Text = "\u00d7";
            lblX.Location = new Point(fieldX + 144, y + 3);
            lblX.Size = new Size(18, 22);
            lblX.ForeColor = mutedText;
            lblX.TextAlign = ContentAlignment.TopCenter;
            Controls.Add(lblX);

            Label lblH = new Label();
            lblH.Text = "\u0412:";
            lblH.Location = new Point(fieldX + 166, y + 4);
            lblH.Size = new Size(20, 22);
            lblH.ForeColor = mutedText;
            Controls.Add(lblH);

            _tbTargetH = new TextBox();
            _tbTargetH.Text = "";
            _tbTargetH.Location = new Point(fieldX + 188, y + 1);
            _tbTargetH.Size = new Size(fieldW - 188, 25);
            _tbTargetH.BackColor = inputBg;
            _tbTargetH.ForeColor = ForeColor;
            _tbTargetH.BorderStyle = BorderStyle.FixedSingle;
            _tbTargetH.TextAlign = HorizontalAlignment.Center;
            _tbTargetH.TabIndex = 1;
            AttachAutoSelect(_tbTargetH);
            Controls.Add(_tbTargetH);

            y += 31;

            Label lblHint = new Label();
            lblHint.Text = "\u041f\u0443\u0441\u0442\u043e \u2014 \u0431\u0435\u0437 \u043e\u0431\u0440\u0435\u0437\u043a\u0438. \u0420\u0435\u0436\u0435\u0442 \u0441\u0438\u043c\u043c\u0435\u0442\u0440\u0438\u0447\u043d\u043e \u043f\u043e \u043a\u0440\u0430\u044f\u043c (520\u00d7620 \u2192 500\u00d7600). DPI \u0438\u0437 \u0444\u0430\u0439\u043b\u0430.";
            lblHint.Location = new Point(pad, y);
            lblHint.Size = new Size(ClientSize.Width - pad * 2, 18);
            lblHint.ForeColor = Color.FromArgb(120, 128, 145);
            lblHint.Font = new Font("Segoe UI", 8f);
            Controls.Add(lblHint);

            y += 24;

            _chkSplit = new CheckBox();
            _chkSplit.Text = "\u0420\u0430\u0437\u0431\u0438\u0432\u0430\u0442\u044c \u043f\u043e\u0441\u0442\u0440\u0430\u043d\u0438\u0447\u043d\u043e";
            _chkSplit.Checked = false;
            _chkSplit.Location = new Point(pad, y);
            _chkSplit.Size = new Size(205, 22);
            _chkSplit.ForeColor = ForeColor;
            _chkSplit.TabIndex = 3;
            Controls.Add(_chkSplit);

            _chkOverprint = new CheckBox();
            _chkOverprint.Text = "\u0421\u0438\u043c\u0443\u043b\u044f\u0446\u0438\u044f \u043e\u0432\u0435\u0440\u043f\u0440\u0438\u043d\u0442\u0430";
            _chkOverprint.Checked = true;
            _chkOverprint.Location = new Point(pad + 215, y);
            _chkOverprint.Size = new Size(210, 22);
            _chkOverprint.ForeColor = ForeColor;
            _chkOverprint.TabIndex = 4;
            Controls.Add(_chkOverprint);

            y += 25;

            _chkRichBlack = new CheckBox();
            _chkRichBlack.Text = "\u0423\u0441\u0438\u043b\u0438\u0442\u044c \u0447\u0451\u0440\u043d\u044b\u0435 \u043f\u043b\u0430\u0448\u043a\u0438 (C40 M30 Y30 K100)";
            _chkRichBlack.Checked = false;
            _chkRichBlack.Location = new Point(pad, y);
            _chkRichBlack.Size = new Size(ClientSize.Width - pad * 2, 22);
            _chkRichBlack.ForeColor = ForeColor;
            _chkRichBlack.TabIndex = 5;
            _toolTip.SetToolTip(_chkRichBlack,
                "\u0411\u0435\u0437\u043e\u043f\u0430\u0441\u043d\u043e \u0443\u0441\u0438\u043b\u0438\u0432\u0430\u0435\u0442 \u0442\u043e\u043b\u044c\u043a\u043e \u043a\u0440\u0443\u043f\u043d\u044b\u0435 \u0441\u043f\u043b\u043e\u0448\u043d\u044b\u0435 \u0437\u0430\u043b\u0438\u0432\u043a\u0438 0/0/0/100.\r\n" +
                "\u041c\u0435\u043b\u043a\u0438\u0439 \u0442\u0435\u043a\u0441\u0442, \u0442\u043e\u043d\u043a\u0438\u0435 \u043b\u0438\u043d\u0438\u0438, \u0444\u043e\u0442\u043e \u0438 \u043a\u0440\u0430\u044f \u0432\u044b\u0432\u043e\u0440\u043e\u0442\u043e\u043a \u043d\u0435 \u0437\u0430\u0442\u0440\u0430\u0433\u0438\u0432\u0430\u044e\u0442\u0441\u044f.");
            Controls.Add(_chkRichBlack);

            y += 28;

            _dropZone = new Panel();
            _dropZone.SuspendLayout();
            _dropZone.Location = new Point(pad, y);
            _dropZone.Size = new Size(ClientSize.Width - pad * 2, 134);
            _dropZone.BackColor = Color.FromArgb(36, 38, 46);
            _dropZone.AllowDrop = true;
            _dropZone.Cursor = Cursors.Hand;
            _dropZone.Paint += DropZone_Paint;
            _dropZone.DragEnter += DropZone_DragEnter;
            _dropZone.DragLeave += DropZone_DragLeave;
            _dropZone.DragDrop += DropZone_DragDrop;
            _dropZone.Click += DropZone_Click;
            Controls.Add(_dropZone);

            _lblDropMain = new Label();
            _lblDropMain.Text = "\u041f\u0435\u0440\u0435\u0442\u0430\u0449\u0438\u0442\u0435 PDF \u0441\u044e\u0434\u0430";
            _lblDropMain.Font = new Font("Segoe UI", 12.5f, FontStyle.Bold);
            _lblDropMain.ForeColor = ForeColor;
            _lblDropMain.TextAlign = ContentAlignment.MiddleCenter;
            _lblDropMain.Location = new Point(10, 32);
            _lblDropMain.Size = new Size(_dropZone.Width - 20, 34);
            _lblDropMain.BackColor = Color.Transparent;
            _lblDropMain.Click += DropZone_Click;
            _dropZone.Controls.Add(_lblDropMain);

            _lblDropSub = new Label();
            _lblDropSub.Text = "\u0438\u043b\u0438 \u043d\u0430\u0436\u043c\u0438\u0442\u0435 \u0434\u043b\u044f \u0432\u044b\u0431\u043e\u0440\u0430 \u2022 TIFF CMYK LZW, \u043d\u0430\u0442\u0438\u0432\u043d\u044b\u0439 DPI";
            _lblDropSub.Font = new Font("Segoe UI", 8.5f);
            _lblDropSub.ForeColor = mutedText;
            _lblDropSub.TextAlign = ContentAlignment.MiddleCenter;
            _lblDropSub.Location = new Point(10, 70);
            _lblDropSub.Size = new Size(_dropZone.Width - 20, 26);
            _lblDropSub.BackColor = Color.Transparent;
            _lblDropSub.Click += DropZone_Click;
            _dropZone.Controls.Add(_lblDropSub);
            _dropZone.ResumeLayout(false);

            y += 144;

            _tbLog = new TextBox();
            _tbLog.Multiline = true;
            _tbLog.ReadOnly = true;
            _tbLog.TabStop = false;
            _tbLog.ScrollBars = ScrollBars.Vertical;
            _tbLog.Location = new Point(pad, y);
            _tbLog.Size = new Size(ClientSize.Width - pad * 2, ClientSize.Height - y - pad);
            _tbLog.BackColor = Color.FromArgb(20, 21, 26);
            _tbLog.ForeColor = Color.FromArgb(185, 195, 210);
            _tbLog.BorderStyle = BorderStyle.FixedSingle;
            _tbLog.Font = new Font("Consolas", 8.5f);
            Controls.Add(_tbLog);

            AllowDrop = true;
            DragEnter += DropZone_DragEnter;
            DragDrop += DropZone_DragDrop;
            Shown += MainForm_Shown;
            FormClosing += MainForm_FormClosing;

            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ResumeLayout(false);
            PerformLayout();
        }

        private void UpdateTopMostButton()
        {
            if (TopMost)
            {
                _btnTopMost.Text = "\u041f\u043e\u0432\u0435\u0440\u0445: \u0412\u041a\u041b";
                _btnTopMost.BackColor = Color.FromArgb(46, 108, 196);
                _btnTopMost.ForeColor = Color.White;
                _btnTopMost.FlatAppearance.BorderColor = Color.FromArgb(95, 160, 245);
            }
            else
            {
                _btnTopMost.Text = "\u041f\u043e\u0432\u0435\u0440\u0445 \u043e\u043a\u043e\u043d";
                _btnTopMost.BackColor = Color.FromArgb(42, 44, 52);
                _btnTopMost.ForeColor = Color.FromArgb(180, 186, 200);
                _btnTopMost.FlatAppearance.BorderColor = Color.FromArgb(74, 78, 92);
            }
        }

        private static void AttachAutoSelect(TextBox tb)
        {
            bool selectOnMouseUp = false;
            tb.Enter += (s, e) =>
            {
                tb.SelectAll();
                selectOnMouseUp = (MouseButtons == MouseButtons.Left);
            };
            tb.MouseUp += (s, e) =>
            {
                if (selectOnMouseUp && tb.SelectionLength == 0)
                    tb.SelectAll();
                selectOnMouseUp = false;
            };
        }

        private void LoadResources()
        {
            _gsPath = FindGhostscript();
            List<ProfileItem> profiles = FindProfiles();

            foreach (ProfileItem item in profiles)
                _cbProfile.Items.Add(item);

            if (_cbProfile.Items.Count > 0)
            {
                int preferred = 0;
                for (int i = 0; i < profiles.Count; i++)
                {
                    if (profiles[i].Name.IndexOf("USWebCoatedSWOP", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        preferred = i;
                        break;
                    }
                }
                _cbProfile.SelectedIndex = preferred;
            }

            if (_gsPath != null)
                AppendLog("Ghostscript: " + _gsPath);
            else
                AppendLog("[!] Ghostscript (gswin64c.exe) \u043d\u0435 \u043d\u0430\u0439\u0434\u0435\u043d.");
        }

        private string FindGhostscript()
        {
            string[] localCandidates = new string[]
            {
                Path.Combine(_baseDir, "engine", "gswin64c.exe"),
                Path.Combine(_baseDir, "gswin64c.exe")
            };

            foreach (string path in localCandidates)
            {
                if (File.Exists(path))
                    return path;
            }

            string[] programDirs = new string[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            };

            foreach (string pf in programDirs)
            {
                if (string.IsNullOrEmpty(pf)) continue;
                string gsRoot = Path.Combine(pf, "gs");
                if (!Directory.Exists(gsRoot)) continue;

                try
                {
                    string[] dirs = Directory.GetDirectories(gsRoot, "gs*");
                    Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
                    Array.Reverse(dirs);
                    foreach (string dir in dirs)
                    {
                        string exe = Path.Combine(dir, "bin", "gswin64c.exe");
                        if (File.Exists(exe))
                            return exe;
                    }
                }
                catch { }
            }

            return null;
        }

        private List<ProfileItem> FindProfiles()
        {
            List<ProfileItem> list = new List<ProfileItem>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string[] folders = new string[]
            {
                _baseDir,
                Path.Combine(_baseDir, "profiles")
            };

            foreach (string folder in folders)
            {
                if (!Directory.Exists(folder)) continue;
                try
                {
                    string[] files = Directory.GetFiles(folder, "*.*")
                        .Where(f => f.EndsWith(".icc", StringComparison.OrdinalIgnoreCase) ||
                                    f.EndsWith(".icm", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                        .ToArray();

                    foreach (string file in files)
                    {
                        string name = Path.GetFileName(file);
                        if (seen.Add(name))
                        {
                            list.Add(new ProfileItem { Name = name, FullPath = file });
                        }
                    }
                }
                catch { }
            }

            return list;
        }

        private bool ValidateEnvironment()
        {
            if (string.IsNullOrEmpty(_gsPath) || !File.Exists(_gsPath))
            {
                MessageBox.Show(
                    "Ghostscript (gswin64c.exe) \u043d\u0435 \u043d\u0430\u0439\u0434\u0435\u043d!\r\n\r\n" +
                    "\u041f\u043e\u043b\u043e\u0436\u0438\u0442\u0435 gswin64c.exe \u0438 gsdll64.dll \u0432 \u043f\u0430\u043f\u043a\u0443 engine\\ \u0440\u044f\u0434\u043e\u043c \u0441 \u043f\u0440\u043e\u0433\u0440\u0430\u043c\u043c\u043e\u0439\r\n" +
                    "\u0438\u043b\u0438 \u0443\u0441\u0442\u0430\u043d\u043e\u0432\u0438\u0442\u0435 Ghostscript (64-bit) \u0432 C:\\Program Files\\gs\\.",
                    "\u041d\u0435 \u043d\u0430\u0439\u0434\u0435\u043d Ghostscript",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return false;
            }

            if (_cbProfile.SelectedItem == null)
            {
                MessageBox.Show(
                    "\u041d\u0435 \u043d\u0430\u0439\u0434\u0435\u043d \u043d\u0438 \u043e\u0434\u0438\u043d ICC-\u043f\u0440\u043e\u0444\u0438\u043b\u044c (.icc / .icm)!\r\n\r\n" +
                    "\u041f\u043e\u043b\u043e\u0436\u0438\u0442\u0435 USWebCoatedSWOP.icc \u0440\u044f\u0434\u043e\u043c \u0441 .exe \u0438\u043b\u0438 \u0432 \u043f\u0430\u043f\u043a\u0443 profiles\\.",
                    "\u041d\u0435 \u043d\u0430\u0439\u0434\u0435\u043d ICC-\u043f\u0440\u043e\u0444\u0438\u043b\u044c",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return false;
            }

            return true;
        }

        private async void MainForm_Shown(object sender, EventArgs e)
        {
            if (!ValidateEnvironment())
            {
                if (_autoCloseOnDone) Close();
                return;
            }

            if (_startupFiles.Length > 0)
            {
                await ProcessFilesAsync(_startupFiles);
                if (_autoCloseOnDone) Close();
            }
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (_cts != null)
            {
                try { _cts.Cancel(); } catch { }
            }

            Process p = _activeProcess;
            if (p != null)
            {
                try
                {
                    if (!p.HasExited) p.Kill();
                }
                catch { }
            }
        }

        private void DropZone_Paint(object sender, PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle rect = new Rectangle(2, 2, _dropZone.Width - 5, _dropZone.Height - 5);
            Color borderColor = _isBusy
                ? Color.FromArgb(230, 175, 70)
                : (_isDragOver ? Color.FromArgb(88, 166, 255) : Color.FromArgb(95, 102, 120));

            using (Pen pen = new Pen(borderColor, 2f))
            {
                pen.DashStyle = DashStyle.Dash;
                e.Graphics.DrawRectangle(pen, rect);
            }
        }

        private void DropZone_DragEnter(object sender, DragEventArgs e)
        {
            if (_isBusy)
            {
                e.Effect = DragDropEffects.None;
                return;
            }

            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null && files.Any(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)))
                {
                    e.Effect = DragDropEffects.Copy;
                    _isDragOver = true;
                    _dropZone.BackColor = Color.FromArgb(44, 52, 68);
                    _dropZone.Invalidate();
                    return;
                }
            }
            e.Effect = DragDropEffects.None;
        }

        private void DropZone_DragLeave(object sender, EventArgs e)
        {
            _isDragOver = false;
            _dropZone.BackColor = Color.FromArgb(36, 38, 46);
            _dropZone.Invalidate();
        }

        private async void DropZone_DragDrop(object sender, DragEventArgs e)
        {
            _isDragOver = false;
            _dropZone.BackColor = Color.FromArgb(36, 38, 46);
            _dropZone.Invalidate();

            if (_isBusy) return;

            string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null) return;

            string[] pdfs = files
                .Where(f => File.Exists(f) && f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (pdfs.Length == 0)
            {
                SetStatus("\u041f\u043e\u0434\u0434\u0435\u0440\u0436\u0438\u0432\u0430\u044e\u0442\u0441\u044f \u0442\u043e\u043b\u044c\u043a\u043e PDF", "\u041f\u0435\u0440\u0435\u0442\u0430\u0449\u0438\u0442\u0435 .pdf \u0444\u0430\u0439\u043b\u044b", Color.FromArgb(240, 120, 120));
                return;
            }

            await ProcessFilesAsync(pdfs);
        }

        private async void DropZone_Click(object sender, EventArgs e)
        {
            if (_isBusy) return;

            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Filter = "PDF \u0444\u0430\u0439\u043b\u044b (*.pdf)|*.pdf";
                ofd.Multiselect = true;
                ofd.Title = "\u0412\u044b\u0431\u0435\u0440\u0438\u0442\u0435 PDF \u0434\u043b\u044f \u043a\u043e\u043d\u0432\u0435\u0440\u0442\u0430\u0446\u0438\u0438";
                if (ofd.ShowDialog(this) == DialogResult.OK && ofd.FileNames.Length > 0)
                {
                    await ProcessFilesAsync(ofd.FileNames);
                }
            }
        }

        private static bool TryParseOptionalMm(string text, out double valueMm)
        {
            valueMm = 0.0;
            string raw = (text ?? "").Trim().Replace(',', '.');
            if (string.IsNullOrEmpty(raw))
                return true;

            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out valueMm) || valueMm < 0)
                return false;

            return true;
        }

        private async Task ProcessFilesAsync(string[] pdfFiles)
        {
            if (!ValidateEnvironment()) return;

            double targetWMm, targetHMm;
            if (!TryParseOptionalMm(_tbTargetW.Text, out targetWMm) ||
                !TryParseOptionalMm(_tbTargetH.Text, out targetHMm))
            {
                MessageBox.Show(
                    "\u0412\u0432\u0435\u0434\u0438\u0442\u0435 \u043a\u043e\u0440\u0440\u0435\u043a\u0442\u043d\u0443\u044e \u0448\u0438\u0440\u0438\u043d\u0443 \u0438 \u0432\u044b\u0441\u043e\u0442\u0443 \u0432 \u043c\u043c (\u043d\u0430\u043f\u0440\u0438\u043c\u0435\u0440, 500 \u0438 600) \u0438\u043b\u0438 \u043e\u0441\u0442\u0430\u0432\u044c\u0442\u0435 \u043f\u043e\u043b\u044f \u043f\u0443\u0441\u0442\u044b\u043c\u0438.",
                    "\u041e\u0448\u0438\u0431\u043a\u0430",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            ProfileItem profile = (ProfileItem)_cbProfile.SelectedItem;
            bool splitPages = _chkSplit.Checked;
            bool overprint = _chkOverprint.Checked;
            bool enhanceBlack = _chkRichBlack.Checked;

            SetBusy(true);
            _cts = new CancellationTokenSource();
            int okCount = 0;
            int errCount = 0;

            try
            {
                for (int i = 0; i < pdfFiles.Length; i++)
                {
                    if (_cts.IsCancellationRequested) break;

                    string pdfPath = pdfFiles[i];
                    string name = Path.GetFileName(pdfPath);
                    int idxCopy = i + 1;
                    int totalCopy = pdfFiles.Length;
                    SetStatus(
                        string.Format("\u041a\u043e\u043d\u0432\u0435\u0440\u0442\u0430\u0446\u0438\u044f ({0}/{1})...", idxCopy, totalCopy),
                        name,
                        Color.FromArgb(240, 195, 90));

                    try
                    {
                        ConversionResult result = await Task.Run(() =>
                            ConvertSinglePdf(
                                pdfPath,
                                profile.FullPath,
                                targetWMm,
                                targetHMm,
                                splitPages,
                                overprint,
                                enhanceBlack,
                                stage => SetStatus(
                                    string.Format("{0} ({1}/{2})...", stage, idxCopy, totalCopy),
                                    name,
                                    Color.FromArgb(240, 195, 90)),
                                _cts.Token));
                        okCount++;
                        AppendLog("OK: " + name + " \u2014 " + result.Summary);
                        foreach (string notice in result.Notices)
                            AppendLog("    " + notice);
                    }
                    catch (Exception ex)
                    {
                        errCount++;
                        AppendLog("\u041e\u0428\u0418\u0411\u041a\u0410: " + name + " \u2014 " + ex.Message);
                    }
                }

                if (errCount == 0)
                {
                    SetStatus(
                        "\u0413\u043e\u0442\u043e\u0432\u043e!",
                        string.Format("\u041e\u0431\u0440\u0430\u0431\u043e\u0442\u0430\u043d\u043e \u0444\u0430\u0439\u043b\u043e\u0432: {0} \u2022 \u041f\u0435\u0440\u0435\u0442\u0430\u0449\u0438\u0442\u0435 \u0441\u043b\u0435\u0434\u0443\u044e\u0449\u0438\u0435 PDF", okCount),
                        Color.FromArgb(110, 215, 145));
                }
                else
                {
                    SetStatus(
                        string.Format("\u0417\u0430\u0432\u0435\u0440\u0448\u0435\u043d\u043e (\u043e\u0448\u0438\u0431\u043e\u043a: {0})", errCount),
                        string.Format("\u0423\u0441\u043f\u0435\u0448\u043d\u043e: {0}, \u0441 \u043e\u0448\u0438\u0431\u043a\u043e\u0439: {1} (\u0441\u043c. \u043b\u043e\u0433 \u043d\u0438\u0436\u0435)", okCount, errCount),
                        Color.FromArgb(240, 120, 120));

                    if (_autoCloseOnDone)
                    {
                        MessageBox.Show(_tbLog.Text, "\u041e\u0448\u0438\u0431\u043a\u0438 \u043a\u043e\u043d\u0432\u0435\u0440\u0442\u0430\u0446\u0438\u0438", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
            }
            finally
            {
                if (_cts != null)
                {
                    _cts.Dispose();
                    _cts = null;
                }
                SetBusy(false);
            }
        }

        private ConversionResult ConvertSinglePdf(
            string inputPdf,
            string iccPath,
            double targetWMm,
            double targetHMm,
            bool splitPages,
            bool overprint,
            bool enhanceBlack,
            Action<string> reportStage,
            CancellationToken token)
        {
            List<PageSizePt> pages = QueryPageSizes(inputPdf, token);
            if (pages.Count == 0)
                throw new InvalidOperationException("\u041d\u0435 \u0443\u0434\u0430\u043b\u043e\u0441\u044c \u043f\u0440\u043e\u0447\u0438\u0442\u0430\u0442\u044c \u0441\u0442\u0440\u0430\u043d\u0438\u0446\u044b PDF.");

            NativeDpiInfo dpiInfo = DetectNativeDpi(inputPdf, pages[0].WidthPt * pages[0].HeightPt, token);
            int dpi = dpiInfo.Dpi;

            string dir = Path.GetDirectoryName(inputPdf);
            string stem = Path.GetFileNameWithoutExtension(inputPdf);
            bool wantCrop = targetWMm > 0 || targetHMm > 0;
            bool uniformSize = AreAllPagesSameSize(pages);
            bool effectiveSplit = splitPages && pages.Count > 1;

            double w0Pt = pages[0].WidthPt;
            double h0Pt = pages[0].HeightPt;
            double outW0Pt, outH0Pt, trimX0Pt, trimY0Pt;
            ComputeCenteredCrop(w0Pt, h0Pt, targetWMm, targetHMm, out outW0Pt, out outH0Pt, out trimX0Pt, out trimY0Pt);

            List<string> outputFiles = new List<string>();

            if (!wantCrop || uniformSize)
            {
                string outTemplate = effectiveSplit
                    ? Path.Combine(dir, EscapePercent(stem) + "_page_%03d.tif")
                    : Path.Combine(dir, EscapePercent(stem) + ".tif");

                bool applyCrop = trimX0Pt > 0.05 || trimY0Pt > 0.05;
                string args = BuildRenderArgs(
                    inputPdf, outTemplate, iccPath, dpi, overprint,
                    applyCrop ? (double?)outW0Pt : null,
                    applyCrop ? (double?)outH0Pt : null,
                    trimX0Pt, trimY0Pt, null);

                RunGhostscript(args, token);

                if (effectiveSplit)
                {
                    for (int i = 0; i < pages.Count; i++)
                        outputFiles.Add(Path.Combine(dir, stem + string.Format("_page_{0:000}.tif", i + 1)));
                }
                else
                {
                    outputFiles.Add(Path.Combine(dir, stem + ".tif"));
                }
            }
            else
            {
                for (int i = 0; i < pages.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    double outWPt, outHPt, trimXPt, trimYPt;
                    ComputeCenteredCrop(pages[i].WidthPt, pages[i].HeightPt, targetWMm, targetHMm, out outWPt, out outHPt, out trimXPt, out trimYPt);
                    bool applyCrop = trimXPt > 0.05 || trimYPt > 0.05;

                    string outPageGs = Path.Combine(dir, EscapePercent(stem) + string.Format("_page_{0:000}.tif", i + 1));
                    string outPageReal = Path.Combine(dir, stem + string.Format("_page_{0:000}.tif", i + 1));
                    string args = BuildRenderArgs(
                        inputPdf, outPageGs, iccPath, dpi, overprint,
                        applyCrop ? (double?)outWPt : null,
                        applyCrop ? (double?)outHPt : null,
                        trimXPt, trimYPt, i + 1);

                    RunGhostscript(args, token);
                    outputFiles.Add(outPageReal);
                }
            }

            if (reportStage != null)
            {
                reportStage(enhanceBlack
                    ? "\u041f\u0440\u043e\u0432\u0435\u0440\u043a\u0430 \u0438 \u0443\u0441\u0438\u043b\u0435\u043d\u0438\u0435 \u0447\u0451\u0440\u043d\u043e\u0433\u043e"
                    : "\u041f\u0440\u0435\u0444\u043b\u0430\u0439\u0442-\u043f\u0440\u043e\u0432\u0435\u0440\u043a\u0430");
            }

            PreflightReport preflight = new PreflightReport();
            foreach (string tifPath in outputFiles)
            {
                token.ThrowIfCancellationRequested();
                if (!File.Exists(tifPath)) continue;
                TiffColorProcessor.AnalyzeAndOptionallyEnhance(tifPath, iccPath, dpi, enhanceBlack, preflight, token);
            }

            double origW0Mm = w0Pt / PtPerMm;
            double origH0Mm = h0Pt / PtPerMm;
            double finalW0Mm = outW0Pt / PtPerMm;
            double finalH0Mm = outH0Pt / PtPerMm;
            bool wasCropped = trimX0Pt > 0.05 || trimY0Pt > 0.05;

            string sizeInfo = wasCropped
                ? string.Format(CultureInfo.InvariantCulture, "{0:0.#}\u00d7{1:0.#} \u2192 {2:0.#}\u00d7{3:0.#} \u043c\u043c", origW0Mm, origH0Mm, finalW0Mm, finalH0Mm)
                : string.Format(CultureInfo.InvariantCulture, "{0:0.#}\u00d7{1:0.#} \u043c\u043c", origW0Mm, origH0Mm);

            string warn = "";
            if (wantCrop && !wasCropped)
                warn = " (\u0444\u0430\u0439\u043b \u043c\u0435\u043d\u044c\u0448\u0435 \u0446\u0435\u043b\u0438 \u2014 \u0431\u0435\u0437 \u043e\u0431\u0440\u0435\u0437\u043a\u0438)";

            string pageInfo = pages.Count > 1 ? string.Format(", {0} \u0441\u0442\u0440.", pages.Count) : "";
            ConversionResult result = new ConversionResult();
            result.Summary = string.Format("{0}{1}{2}, {3} DPI ({4})", sizeInfo, warn, pageInfo, dpi, dpiInfo.SourceLabel);
            result.Notices = TiffColorProcessor.BuildNotices(preflight, enhanceBlack);
            return result;
        }

        private static void ComputeCenteredCrop(
            double origWPt, double origHPt,
            double targetWMm, double targetHMm,
            out double outWPt, out double outHPt,
            out double trimXPt, out double trimYPt)
        {
            double desiredWPt = targetWMm > 0 ? targetWMm * PtPerMm : origWPt;
            double desiredHPt = targetHMm > 0 ? targetHMm * PtPerMm : origHPt;

            outWPt = desiredWPt < origWPt - 0.1 ? desiredWPt : origWPt;
            outHPt = desiredHPt < origHPt - 0.1 ? desiredHPt : origHPt;

            trimXPt = Math.Max(0.0, (origWPt - outWPt) / 2.0);
            trimYPt = Math.Max(0.0, (origHPt - outHPt) / 2.0);
        }

        private List<PageSizePt> QueryPageSizes(string inputPdf, CancellationToken token)
        {
            string args =
                "-q -dSAFER -dBATCH -dNOPAUSE -dUseTrimBox -sDEVICE=nullpage " +
                "-c \"<</EndPage { exch pop 0 eq dup { currentpagedevice /PageSize get aload pop (PAGESIZE:) print exch =only (,) print = } if }>> setpagedevice\" " +
                "-f \"" + inputPdf + "\"";

            string stdout = RunGhostscript(args, token);
            List<PageSizePt> pages = new List<PageSizePt>();

            using (StringReader sr = new StringReader(stdout))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    int idx = line.IndexOf("PAGESIZE:", StringComparison.Ordinal);
                    if (idx < 0) continue;
                    string payload = line.Substring(idx + 9).Trim();
                    string[] parts = payload.Split(',');
                    double w, h;
                    if (parts.Length == 2 &&
                        double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out w) &&
                        double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out h))
                    {
                        pages.Add(new PageSizePt { WidthPt = w, HeightPt = h });
                    }
                }
            }

            return pages;
        }

        private NativeDpiInfo DetectNativeDpi(string inputPdf, double pageAreaPt2, CancellationToken token)
        {
            // Probe page 1 with -dPDFDEBUG on nullpage to inspect embedded image dimensions, placement CTM, and vector content.
            string args =
                "-q -dSAFER -dBATCH -dNOPAUSE -dUseTrimBox -dFirstPage=1 -dLastPage=1 " +
                "-sDEVICE=nullpage -dPDFDEBUG -f \"" + inputPdf + "\"";

            List<string> lines = RunGhostscriptStderrProbe(args, token, 150000, 6000);
            if (lines.Count == 0)
                return new NativeDpiInfo { Dpi = DefaultVectorDpi, SourceLabel = "\u0432\u0435\u043a\u0442\u043e\u0440" };

            // Pass 1: collect all Image XObject definitions (objNum -> WidthPx, HeightPx, IsMask), SMask refs, & UserUnit.
            Dictionary<int, PdfImageInfo> images = new Dictionary<int, PdfImageInfo>();
            HashSet<int> smaskObjs = new HashSet<int>();
            double userUnit = 1.0;

            int currentObj = -1;
            StringBuilder objBuf = new StringBuilder();
            bool inObj = false;

            foreach (string line in lines)
            {
                Match mStart = RxObjStart.Match(line);
                if (mStart.Success)
                {
                    if (inObj && currentObj >= 0)
                        TryCollectImageOrUnit(currentObj, objBuf.ToString(), images, smaskObjs, ref userUnit);

                    currentObj = int.Parse(mStart.Groups[1].Value, CultureInfo.InvariantCulture);
                    objBuf.Length = 0;
                    inObj = true;
                    continue;
                }

                if (inObj)
                {
                    objBuf.Append(' ').Append(line);
                    if (line.IndexOf("endobj", StringComparison.Ordinal) >= 0 ||
                        line.IndexOf("stream", StringComparison.Ordinal) >= 0)
                    {
                        TryCollectImageOrUnit(currentObj, objBuf.ToString(), images, smaskObjs, ref userUnit);
                        inObj = false;
                        currentObj = -1;
                        objBuf.Length = 0;
                    }
                }
            }

            if (inObj && currentObj >= 0)
                TryCollectImageOrUnit(currentObj, objBuf.ToString(), images, smaskObjs, ref userUnit);

            foreach (int maskId in smaskObjs)
                images.Remove(maskId);

            if (images.Count == 0)
                return new NativeDpiInfo { Dpi = DefaultVectorDpi, SourceLabel = "\u0432\u0435\u043a\u0442\u043e\u0440" };

            // Pass 2: track resource name -> objNum bindings, CTM stack (q / cm / Do / Q), and detect vector/text painting.
            Dictionary<string, int> nameToObj = new Dictionary<string, int>(StringComparer.Ordinal);
            Stack<CtmMatrix> ctmStack = new Stack<CtmMatrix>();
            CtmMatrix ctm = CtmMatrix.Identity();
            Dictionary<int, double> areaByDpi = new Dictionary<int, double>();
            bool inContentStream = false;
            bool hasVectorContent = false;

            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.Length == 0) continue;

                if (line[0] == '%')
                {
                    if (line.IndexOf("content stream", StringComparison.OrdinalIgnoreCase) >= 0)
                        inContentStream = true;
                    continue;
                }

                if (line.IndexOf(" 0 R", StringComparison.Ordinal) >= 0 || line.IndexOf(" R", StringComparison.Ordinal) >= 0)
                {
                    foreach (Match mRef in RxNameRef.Matches(line))
                    {
                        int objId;
                        if (int.TryParse(mRef.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out objId) &&
                            images.ContainsKey(objId))
                        {
                            nameToObj[mRef.Groups[1].Value] = objId;
                        }
                    }
                }

                if (inContentStream && !hasVectorContent &&
                    !line.StartsWith("/", StringComparison.Ordinal) &&
                    !line.StartsWith("Filter:", StringComparison.Ordinal) &&
                    !line.StartsWith("FILTER ", StringComparison.Ordinal) &&
                    RxVectorOp.IsMatch(line))
                {
                    hasVectorContent = true;
                }

                if (line == "q")
                {
                    ctmStack.Push(ctm);
                    continue;
                }
                if (line == "Q")
                {
                    ctm = ctmStack.Count > 0 ? ctmStack.Pop() : CtmMatrix.Identity();
                    continue;
                }

                if (line.IndexOf(" cm", StringComparison.Ordinal) >= 0)
                {
                    foreach (Match mCm in RxCm.Matches(line))
                    {
                        CtmMatrix m = new CtmMatrix
                        {
                            A = double.Parse(mCm.Groups[1].Value, CultureInfo.InvariantCulture),
                            B = double.Parse(mCm.Groups[2].Value, CultureInfo.InvariantCulture),
                            C = double.Parse(mCm.Groups[3].Value, CultureInfo.InvariantCulture),
                            D = double.Parse(mCm.Groups[4].Value, CultureInfo.InvariantCulture),
                            E = double.Parse(mCm.Groups[5].Value, CultureInfo.InvariantCulture),
                            F = double.Parse(mCm.Groups[6].Value, CultureInfo.InvariantCulture)
                        };
                        ctm = CtmMatrix.Multiply(m, ctm);
                    }
                }

                if (line.IndexOf(" Do", StringComparison.Ordinal) >= 0)
                {
                    foreach (Match mDo in RxDo.Matches(line))
                    {
                        string xname = mDo.Groups[1].Value;
                        int objId;
                        PdfImageInfo img;
                        if (nameToObj.TryGetValue(xname, out objId) && images.TryGetValue(objId, out img))
                        {
                            if (img.IsMask || img.WidthPx < 16 || img.HeightPx < 16)
                                continue;

                            double placedWPt = Math.Sqrt(ctm.A * ctm.A + ctm.B * ctm.B) * userUnit;
                            double placedHPt = Math.Sqrt(ctm.C * ctm.C + ctm.D * ctm.D) * userUnit;
                            if (placedWPt < 4.0 || placedHPt < 4.0)
                                continue;

                            double dpiX = img.WidthPx * 72.0 / placedWPt;
                            double dpiY = img.HeightPx * 72.0 / placedHPt;
                            double rawDpi = (dpiX + dpiY) * 0.5;
                            int rounded = (int)Math.Round(rawDpi);
                            if (rounded >= 36 && rounded <= 2400)
                            {
                                double area = placedWPt * placedHPt;
                                double prev;
                                areaByDpi.TryGetValue(rounded, out prev);
                                areaByDpi[rounded] = prev + area;
                            }
                        }
                    }
                }
            }

            if (areaByDpi.Count == 0)
                return new NativeDpiInfo { Dpi = DefaultVectorDpi, SourceLabel = "\u0432\u0435\u043a\u0442\u043e\u0440" };

            int bestDpi = DefaultVectorDpi;
            double bestArea = -1.0;
            int maxSignificantDpi = 0;
            double significantThreshold = Math.Max(100.0, pageAreaPt2 * 0.10);

            foreach (KeyValuePair<int, double> kv in areaByDpi)
            {
                if (kv.Value > bestArea)
                {
                    bestArea = kv.Value;
                    bestDpi = kv.Key;
                }
                if (kv.Value >= significantThreshold && kv.Key > maxSignificantDpi)
                {
                    maxSignificantDpi = kv.Key;
                }
            }

            if (hasVectorContent)
            {
                // Hybrid/vector PDF: never drop below 300 DPI due to a low-res background photo.
                int effectiveDpi = Math.Max(DefaultVectorDpi, Math.Min(1200, maxSignificantDpi));
                string label = effectiveDpi > DefaultVectorDpi ? "\u0432\u0435\u043a\u0442\u043e\u0440+\u0440\u0430\u0441\u0442\u0440" : "\u0432\u0435\u043a\u0442\u043e\u0440";
                return new NativeDpiInfo { Dpi = effectiveDpi, SourceLabel = label };
            }

            // Pure raster PDF: preserve native bitmap DPI (clamped safely to 72..1200).
            int rasterDpi = Math.Max(72, Math.Min(1200, bestDpi));
            return new NativeDpiInfo { Dpi = rasterDpi, SourceLabel = "\u0440\u0430\u0441\u0442\u0440 \u0438\u0437 \u0444\u0430\u0439\u043b\u0430" };
        }

        private static void TryCollectImageOrUnit(
            int objNum,
            string dictText,
            Dictionary<int, PdfImageInfo> images,
            HashSet<int> smaskObjs,
            ref double userUnit)
        {
            if (dictText.IndexOf("/UserUnit", StringComparison.Ordinal) >= 0)
            {
                Match mu = RxUserUnit.Match(dictText);
                double u;
                if (mu.Success && double.TryParse(mu.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out u) && u > 0)
                    userUnit = u;
            }

            if (dictText.IndexOf("/SMask", StringComparison.Ordinal) >= 0)
            {
                foreach (Match ms in RxSMaskRef.Matches(dictText))
                {
                    int maskId;
                    if (int.TryParse(ms.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out maskId))
                        smaskObjs.Add(maskId);
                }
            }

            if (dictText.IndexOf("/Subtype", StringComparison.Ordinal) < 0 ||
                dictText.IndexOf("/Image", StringComparison.Ordinal) < 0)
                return;

            Match mw = RxWidth.Match(dictText);
            Match mh = RxHeight.Match(dictText);
            if (!mw.Success || !mh.Success) return;

            int w, h;
            if (!int.TryParse(mw.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out w) ||
                !int.TryParse(mh.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out h))
                return;

            bool isMask = dictText.IndexOf("/ImageMask true", StringComparison.Ordinal) >= 0;
            images[objNum] = new PdfImageInfo { WidthPx = w, HeightPx = h, IsMask = isMask };
        }

        private List<string> RunGhostscriptStderrProbe(string arguments, CancellationToken token, int maxLines, int timeoutMs)
        {
            List<string> lines = new List<string>();
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = _gsPath;
            psi.Arguments = arguments;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;

            bool limitHit = false;
            using (Process p = new Process())
            {
                p.StartInfo = psi;
                p.OutputDataReceived += (s, e) => { };
                p.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data == null || limitHit) return;
                    if (lines.Count >= maxLines)
                    {
                        limitHit = true;
                        try { p.Kill(); } catch { }
                        return;
                    }
                    lines.Add(e.Data);
                };

                _activeProcess = p;
                try
                {
                    Stopwatch sw = Stopwatch.StartNew();
                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();

                    while (!p.WaitForExit(50))
                    {
                        if (token.IsCancellationRequested || sw.ElapsedMilliseconds > timeoutMs || limitHit)
                        {
                            try { p.Kill(); } catch { }
                            if (token.IsCancellationRequested) token.ThrowIfCancellationRequested();
                            break;
                        }
                    }
                    try { p.WaitForExit(500); } catch { }
                }
                finally
                {
                    _activeProcess = null;
                }
            }

            return lines;
        }

        private static bool AreAllPagesSameSize(List<PageSizePt> pages)
        {
            if (pages.Count <= 1) return true;
            double w0 = pages[0].WidthPt;
            double h0 = pages[0].HeightPt;
            for (int i = 1; i < pages.Count; i++)
            {
                if (Math.Abs(pages[i].WidthPt - w0) > 0.5 || Math.Abs(pages[i].HeightPt - h0) > 0.5)
                    return false;
            }
            return true;
        }

        private static string EscapePercent(string text)
        {
            return text.Replace("%", "%%");
        }

        private static string BuildRenderArgs(
            string inputPdf,
            string outputPath,
            string iccPath,
            int dpi,
            bool overprint,
            double? targetWidthPt,
            double? targetHeightPt,
            double trimXPt,
            double trimYPt,
            int? singlePageNumber)
        {
            int threads = Math.Max(1, Math.Min(8, Environment.ProcessorCount));
            StringBuilder sb = new StringBuilder();
            sb.Append("-q -dSAFER -dBATCH -dNOPAUSE -dUseTrimBox ");
            sb.Append("-sDEVICE=tiff32nc ");
            sb.AppendFormat(CultureInfo.InvariantCulture, "-r{0} ", dpi);
            sb.Append("-sCompression=lzw -dMaxStripSize=262144 ");
            sb.Append(overprint ? "-dOverprint=/simulate " : "-dOverprint=/disable ");
            sb.Append("-dTextAlphaBits=4 -dGraphicsAlphaBits=4 -dDOINTERPOLATE ");
            sb.AppendFormat("-sDefaultCMYKProfile=\"{0}\" -sOutputICCProfile=\"{0}\" ", iccPath);
            sb.Append("-dRenderIntent=1 -dBlackPtComp=1 ");
            sb.AppendFormat(CultureInfo.InvariantCulture, "-dNumRenderingThreads={0} ", threads);
            sb.Append("-dMaxBitmap=100000000 -dBufferSpace=256000000 ");

            if (singlePageNumber.HasValue)
            {
                sb.AppendFormat(CultureInfo.InvariantCulture, "-dFirstPage={0} -dLastPage={0} ", singlePageNumber.Value);
            }

            bool applyCrop = targetWidthPt.HasValue && targetHeightPt.HasValue && (trimXPt > 0.05 || trimYPt > 0.05);
            if (applyCrop)
            {
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "-dDEVICEWIDTHPOINTS={0:0.####} -dDEVICEHEIGHTPOINTS={1:0.####} -dFIXEDMEDIA ",
                    targetWidthPt.Value, targetHeightPt.Value);
            }

            sb.AppendFormat("-sOutputFile=\"{0}\" ", outputPath);

            if (applyCrop)
            {
                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "-c \"<</Install {{ -{0:0.####} -{1:0.####} translate }}>> setpagedevice\" ",
                    trimXPt, trimYPt);
            }

            sb.AppendFormat("-f \"{0}\"", inputPdf);
            return sb.ToString();
        }

        private string RunGhostscript(string arguments, CancellationToken token)
        {
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = _gsPath;
            psi.Arguments = arguments;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;

            StringBuilder stdout = new StringBuilder();
            StringBuilder stderr = new StringBuilder();

            using (Process p = new Process())
            {
                p.StartInfo = psi;
                p.OutputDataReceived += (s, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

                _activeProcess = p;
                try
                {
                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();

                    while (!p.WaitForExit(100))
                    {
                        if (token.IsCancellationRequested)
                        {
                            try { p.Kill(); } catch { }
                            token.ThrowIfCancellationRequested();
                        }
                    }
                    p.WaitForExit();

                    if (p.ExitCode != 0)
                    {
                        string err = stderr.ToString().Trim();
                        if (string.IsNullOrEmpty(err)) err = stdout.ToString().Trim();
                        if (err.Length > 300) err = err.Substring(0, 300) + "...";
                        throw new InvalidOperationException("Ghostscript (\u043a\u043e\u0434 " + p.ExitCode + "): " + err);
                    }
                }
                finally
                {
                    _activeProcess = null;
                }
            }

            return stdout.ToString();
        }

        private void SetBusy(bool busy)
        {
            _isBusy = busy;
            _cbProfile.Enabled = !busy;
            _tbTargetW.Enabled = !busy;
            _tbTargetH.Enabled = !busy;
            _chkSplit.Enabled = !busy;
            _chkOverprint.Enabled = !busy;
            _chkRichBlack.Enabled = !busy;
            _dropZone.Cursor = busy ? Cursors.WaitCursor : Cursors.Hand;
            _dropZone.Invalidate();
        }

        private void SetStatus(string main, string sub, Color color)
        {
            if (InvokeRequired)
            {
                BeginInvoke((Action)(() => SetStatus(main, sub, color)));
                return;
            }
            _lblDropMain.Text = main;
            _lblDropMain.ForeColor = color;
            _lblDropSub.Text = sub;
        }

        private void AppendLog(string message)
        {
            if (InvokeRequired)
            {
                BeginInvoke((Action)(() => AppendLog(message)));
                return;
            }
            _tbLog.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message + Environment.NewLine);
        }
    }

    sealed class ConversionResult
    {
        public string Summary;
        public List<string> Notices = new List<string>();
    }

    sealed class PreflightReport
    {
        public double MaxTilPct;
        public long OverTilPixels;
        public long TotalPixels;
        public long SolidPureKPixels;
        public double SolidPureKAreaMm2;
        public long EnhancedPixels;
        public double EnhancedAreaMm2;
    }

    sealed class FrameAnalysis
    {
        public int Width;
        public int Height;
        public long SolidPureKPixels;
        public double SolidPureKAreaMm2;
        public byte[] MaskBits;
        public int MaskStride;
    }

    sealed class VerticalMorphFilter
    {
        private readonly int _width;
        private readonly int _radius;
        private readonly int _win;
        private readonly bool _isErode;
        private readonly byte[][] _ring;
        private readonly int[] _colSum;
        private readonly byte[] _outRow;
        private readonly byte[] _lastRow;
        private int _count;
        private int _head;
        private int _nextOutY;

        public VerticalMorphFilter(int width, int radius, bool isErode)
        {
            _width = width;
            _radius = radius;
            _win = 2 * radius + 1;
            _isErode = isErode;
            _ring = new byte[_win][];
            for (int i = 0; i < _win; i++)
                _ring[i] = new byte[width];
            _colSum = new int[width];
            _outRow = new byte[width];
            _lastRow = new byte[width];
        }

        public void PushRow(byte[] row, int height, Action<int, byte[]> emit)
        {
            Buffer.BlockCopy(row, 0, _lastRow, 0, _width);

            if (_count == 0)
            {
                int initCopies = _radius + 1;
                for (int c = 0; c < initCopies; c++)
                    Buffer.BlockCopy(row, 0, _ring[c], 0, _width);
                for (int x = 0; x < _width; x++)
                    _colSum[x] = row[x] * initCopies;
                _count = initCopies;
            }
            else if (_count < _win)
            {
                Buffer.BlockCopy(row, 0, _ring[_count], 0, _width);
                for (int x = 0; x < _width; x++)
                    _colSum[x] += row[x];
                _count++;
            }
            else
            {
                byte[] oldRow = _ring[_head];
                for (int x = 0; x < _width; x++)
                {
                    _colSum[x] += row[x] - oldRow[x];
                    oldRow[x] = row[x];
                }
                _head++;
                if (_head == _win) _head = 0;
            }

            if (_count == _win && _nextOutY < height)
            {
                EmitCurrent(emit);
            }
        }

        public void Flush(int height, Action<int, byte[]> emit)
        {
            if (_count == 0) return;

            while (_nextOutY < height)
            {
                if (_count < _win)
                {
                    Buffer.BlockCopy(_lastRow, 0, _ring[_count], 0, _width);
                    for (int x = 0; x < _width; x++)
                        _colSum[x] += _lastRow[x];
                    _count++;
                }
                else
                {
                    byte[] oldRow = _ring[_head];
                    for (int x = 0; x < _width; x++)
                    {
                        _colSum[x] += _lastRow[x] - oldRow[x];
                        oldRow[x] = _lastRow[x];
                    }
                    _head++;
                    if (_head == _win) _head = 0;
                }

                if (_count == _win)
                {
                    EmitCurrent(emit);
                }
            }
        }

        private void EmitCurrent(Action<int, byte[]> emit)
        {
            if (_isErode)
            {
                for (int x = 0; x < _width; x++)
                    _outRow[x] = (_colSum[x] == _win) ? (byte)1 : (byte)0;
            }
            else
            {
                for (int x = 0; x < _width; x++)
                    _outRow[x] = (_colSum[x] > 0) ? (byte)1 : (byte)0;
            }

            int y = _nextOutY++;
            emit(y, _outRow);
        }
    }

    sealed class RichBlackBitmapSource : WpfImaging.BitmapSource
    {
        private const byte RichC = 102; // 40%
        private const byte RichM = 77;  // 30%
        private const byte RichY = 77;  // 30%
        private const byte RichK = 255; // 100%

        private readonly WpfImaging.BitmapSource _source;
        private readonly byte[] _maskBits;
        private readonly int _maskStride;

        public RichBlackBitmapSource(WpfImaging.BitmapSource source, byte[] maskBits, int maskStride)
        {
            _source = source;
            _maskBits = maskBits;
            _maskStride = maskStride;
        }

        public override double DpiX { get { return _source.DpiX; } }
        public override double DpiY { get { return _source.DpiY; } }
        public override int PixelWidth { get { return _source.PixelWidth; } }
        public override int PixelHeight { get { return _source.PixelHeight; } }
        public override WpfMedia.PixelFormat Format { get { return _source.Format; } }
        public override WpfImaging.BitmapPalette Palette { get { return _source.Palette; } }

        protected override WpfFreezable CreateInstanceCore()
        {
            return new RichBlackBitmapSource(_source, _maskBits, _maskStride);
        }

        public override void CopyPixels(WpfInt32Rect sourceRect, Array pixels, int stride, int offset)
        {
            _source.CopyPixels(sourceRect, pixels, stride, offset);
            byte[] buf = pixels as byte[];
            if (buf == null || _maskBits == null) return;

            ApplyMaskToBuffer(buf, sourceRect.X, sourceRect.Y, sourceRect.Width, sourceRect.Height, stride, offset);
        }

        public override void CopyPixels(WpfInt32Rect sourceRect, IntPtr buffer, int bufferSize, int stride)
        {
            if (_maskBits == null)
            {
                _source.CopyPixels(sourceRect, buffer, bufferSize, stride);
                return;
            }

            int width = sourceRect.Width;
            int height = sourceRect.Height;
            int rowBytes = width * 4;
            int bandH = Math.Min(64, height);
            byte[] tmp = new byte[bandH * rowBytes];

            for (int yOff = 0; yOff < height; yOff += bandH)
            {
                int rows = Math.Min(bandH, height - yOff);
                WpfInt32Rect subRect = new WpfInt32Rect(sourceRect.X, sourceRect.Y + yOff, width, rows);
                _source.CopyPixels(subRect, tmp, rowBytes, 0);
                ApplyMaskToBuffer(tmp, sourceRect.X, sourceRect.Y + yOff, width, rows, rowBytes, 0);

                for (int r = 0; r < rows; r++)
                {
                    IntPtr dstPtr = IntPtr.Add(buffer, (yOff + r) * stride);
                    Marshal.Copy(tmp, r * rowBytes, dstPtr, rowBytes);
                }
            }
        }

        private void ApplyMaskToBuffer(byte[] buf, int rectX, int rectY, int rectW, int rectH, int stride, int offset)
        {
            for (int r = 0; r < rectH; r++)
            {
                int maskRowOffset = (rectY + r) * _maskStride;
                int dstRowOffset = offset + r * stride;

                for (int c = 0; c < rectW; c++)
                {
                    int gx = rectX + c;
                    if ((_maskBits[maskRowOffset + (gx >> 3)] & (1 << (gx & 7))) != 0)
                    {
                        int p = dstRowOffset + (c << 2);
                        buf[p] = RichC;
                        buf[p + 1] = RichM;
                        buf[p + 2] = RichY;
                        buf[p + 3] = RichK;
                    }
                }
            }
        }
    }

    static class TiffColorProcessor
    {
        private const int OverTilByteThreshold = 778; // >305.1% (778 / 255 = 305.1%)
        private const double ErodeMm = 0.45;          // Protects thin strokes & text up to ~0.9 mm
        private const double ChokeMm = 0.17;          // Pure-K keepaway around white knockouts

        public static void AnalyzeAndOptionallyEnhance(
            string tifPath,
            string iccPath,
            int fallbackDpi,
            bool enhanceBlack,
            PreflightReport report,
            CancellationToken token)
        {
            string tmpPath = tifPath + ".richblack.tmp";
            bool wroteTemp = false;

            try
            {
                using (FileStream inFs = new FileStream(tifPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    WpfImaging.TiffBitmapDecoder decoder = new WpfImaging.TiffBitmapDecoder(
                        inFs,
                        WpfImaging.BitmapCreateOptions.PreservePixelFormat | WpfImaging.BitmapCreateOptions.IgnoreColorProfile,
                        WpfImaging.BitmapCacheOption.None);

                    int frameCount = decoder.Frames.Count;
                    FrameAnalysis[] analyses = new FrameAnalysis[frameCount];
                    bool anyFrameToEnhance = false;

                    for (int f = 0; f < frameCount; f++)
                    {
                        token.ThrowIfCancellationRequested();
                        WpfImaging.BitmapFrame frame = decoder.Frames[f];
                        if (frame.Format != WpfMedia.PixelFormats.Cmyk32)
                            continue;

                        double frameDpi = frame.DpiX > 10.0 ? frame.DpiX : fallbackDpi;
                        FrameAnalysis fa = AnalyzeFrame(frame, frameDpi, enhanceBlack, report, token);
                        analyses[f] = fa;

                        if (enhanceBlack && fa.MaskBits != null && ShouldEnhanceFrame(fa))
                        {
                            anyFrameToEnhance = true;
                        }
                        else if (fa != null)
                        {
                            // Release mask early if frame does not need enhancement.
                            fa.MaskBits = null;
                        }
                    }

                    if (anyFrameToEnhance)
                    {
                        ReadOnlyCollection<WpfMedia.ColorContext> colorContexts = null;
                        if (!string.IsNullOrEmpty(iccPath) && File.Exists(iccPath))
                        {
                            colorContexts = new ReadOnlyCollection<WpfMedia.ColorContext>(
                                new WpfMedia.ColorContext[] { new WpfMedia.ColorContext(new Uri(iccPath)) });
                        }

                        WpfImaging.TiffBitmapEncoder encoder = new WpfImaging.TiffBitmapEncoder();
                        encoder.Compression = WpfImaging.TiffCompressOption.Lzw;

                        for (int f = 0; f < frameCount; f++)
                        {
                            token.ThrowIfCancellationRequested();
                            WpfImaging.BitmapSource src = decoder.Frames[f];
                            FrameAnalysis fa = analyses[f];

                            if (fa != null && fa.MaskBits != null)
                            {
                                RichBlackBitmapSource wrapped = new RichBlackBitmapSource(src, fa.MaskBits, fa.MaskStride);
                                wrapped.Freeze();
                                src = wrapped;
                                report.EnhancedPixels += fa.SolidPureKPixels;
                                report.EnhancedAreaMm2 += fa.SolidPureKAreaMm2;
                            }

                            encoder.Frames.Add(WpfImaging.BitmapFrame.Create(src, null, null, colorContexts));
                        }

                        using (FileStream outFs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            encoder.Save(outFs);
                        }
                        wroteTemp = true;
                    }
                }

                if (wroteTemp)
                {
                    File.Delete(tifPath);
                    File.Move(tmpPath, tifPath);
                    wroteTemp = false;
                }
            }
            finally
            {
                if (wroteTemp)
                {
                    try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                }
            }
        }

        private static bool ShouldEnhanceFrame(FrameAnalysis fa)
        {
            if (fa.SolidPureKPixels <= 0) return false;
            double totalPx = (double)fa.Width * fa.Height;
            double pct = totalPx > 0 ? (fa.SolidPureKPixels * 100.0 / totalPx) : 0.0;
            return fa.SolidPureKAreaMm2 >= 25.0 || (pct >= 0.5 && fa.SolidPureKAreaMm2 >= 10.0);
        }

        private static FrameAnalysis AnalyzeFrame(
            WpfImaging.BitmapSource frame,
            double dpi,
            bool buildMask,
            PreflightReport report,
            CancellationToken token)
        {
            int width = frame.PixelWidth;
            int height = frame.PixelHeight;
            int stride = width * 4;
            int stripH = 64;
            byte[] stripBuf = new byte[stripH * stride];

            int erodeR = Math.Max(2, (int)Math.Round(dpi * ErodeMm / 25.4));
            int chokeR = Math.Max(1, Math.Min(erodeR - 1, (int)Math.Round(dpi * ChokeMm / 25.4)));
            int dilateR = erodeR - chokeR;

            byte[] rowPureK = new byte[width];
            byte[] rowHorizEroded = new byte[width];
            byte[] rowHorizDilated = new byte[width];

            VerticalMorphFilter vErode = new VerticalMorphFilter(width, erodeR, true);
            VerticalMorphFilter vDilate = new VerticalMorphFilter(width, dilateR, false);

            int maskStride = (width + 7) >> 3;
            byte[] maskBits = null;
            long solidCount = 0;

            int maxTilSum = 0;
            long overTilPx = 0;

            Action<int, byte[]> onDilatedRow = (finalY, finalRow) =>
            {
                int rowSolid = 0;
                for (int x = 0; x < width; x++)
                {
                    if (finalRow[x] != 0) rowSolid++;
                }

                if (rowSolid == 0) return;

                solidCount += rowSolid;
                if (!buildMask) return;

                if (maskBits == null)
                    maskBits = new byte[(long)height * maskStride];

                int maskRowOffset = finalY * maskStride;
                for (int x = 0; x < width; x++)
                {
                    if (finalRow[x] != 0)
                        maskBits[maskRowOffset + (x >> 3)] |= (byte)(1 << (x & 7));
                }
            };

            Action<int, byte[]> onErodedRow = (erodedY, erodedRow) =>
            {
                DilateRow1D(erodedRow, rowHorizDilated, width, dilateR);
                vDilate.PushRow(rowHorizDilated, height, onDilatedRow);
            };

            for (int y0 = 0; y0 < height; y0 += stripH)
            {
                token.ThrowIfCancellationRequested();
                int rows = Math.Min(stripH, height - y0);
                frame.CopyPixels(new WpfInt32Rect(0, y0, width, rows), stripBuf, stride, 0);

                for (int r = 0; r < rows; r++)
                {
                    int rowOff = r * stride;
                    for (int x = 0; x < width; x++)
                    {
                        int p = rowOff + (x << 2);
                        int c = stripBuf[p];
                        int m = stripBuf[p + 1];
                        int yv = stripBuf[p + 2];
                        int k = stripBuf[p + 3];

                        int til = c + m + yv + k;
                        if (til > maxTilSum) maxTilSum = til;
                        if (til > OverTilByteThreshold) overTilPx++;

                        rowPureK[x] = (k >= 250 && c <= 5 && m <= 5 && yv <= 5) ? (byte)1 : (byte)0;
                    }

                    ErodeRow1D(rowPureK, rowHorizEroded, width, erodeR);
                    vErode.PushRow(rowHorizEroded, height, onErodedRow);
                }
            }

            vErode.Flush(height, onErodedRow);
            vDilate.Flush(height, onDilatedRow);

            double maxTilPct = maxTilSum * 100.0 / 255.0;
            if (maxTilPct > report.MaxTilPct) report.MaxTilPct = maxTilPct;
            report.OverTilPixels += overTilPx;
            report.TotalPixels += (long)width * height;

            double pxMm = 25.4 / dpi;
            double solidAreaMm2 = solidCount * pxMm * pxMm;
            report.SolidPureKPixels += solidCount;
            report.SolidPureKAreaMm2 += solidAreaMm2;

            return new FrameAnalysis
            {
                Width = width,
                Height = height,
                SolidPureKPixels = solidCount,
                SolidPureKAreaMm2 = solidAreaMm2,
                MaskBits = maskBits,
                MaskStride = maskStride
            };
        }

        private static void ErodeRow1D(byte[] src, byte[] dst, int width, int r)
        {
            int win = 2 * r + 1;
            int sum = src[0] * (r + 1);
            for (int i = 1; i <= r; i++)
                sum += src[i < width ? i : width - 1];

            for (int x = 0; x < width; x++)
            {
                dst[x] = (sum == win) ? (byte)1 : (byte)0;
                int outIdx = x - r;
                int inIdx = x + r + 1;
                sum -= src[outIdx < 0 ? 0 : outIdx];
                sum += src[inIdx < width ? inIdx : width - 1];
            }
        }

        private static void DilateRow1D(byte[] src, byte[] dst, int width, int r)
        {
            int sum = src[0] * (r + 1);
            for (int i = 1; i <= r; i++)
                sum += src[i < width ? i : width - 1];

            for (int x = 0; x < width; x++)
            {
                dst[x] = (sum > 0) ? (byte)1 : (byte)0;
                int outIdx = x - r;
                int inIdx = x + r + 1;
                sum -= src[outIdx < 0 ? 0 : outIdx];
                sum += src[inIdx < width ? inIdx : width - 1];
            }
        }

        public static List<string> BuildNotices(PreflightReport report, bool enhanceBlack)
        {
            List<string> notices = new List<string>();
            if (report == null || report.TotalPixels <= 0)
                return notices;

            double overPct = report.OverTilPixels * 100.0 / report.TotalPixels;
            if (report.MaxTilPct > 305.5 && (report.OverTilPixels >= 100 || overPct >= 0.01))
            {
                notices.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "[!] \u041f\u0435\u0440\u0435\u043b\u0438\u0432 \u043a\u0440\u0430\u0441\u043a\u0438: TIL \u0434\u043e {0:0}% (>305% \u043d\u0430 {1:0.##}% \u043f\u043b\u043e\u0449\u0430\u0434\u0438)",
                    report.MaxTilPct, overPct));
            }

            if (report.EnhancedPixels > 0)
            {
                double enhPct = report.EnhancedPixels * 100.0 / report.TotalPixels;
                double enhCm2 = report.EnhancedAreaMm2 / 100.0;
                notices.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    "[i] \u0427\u0451\u0440\u043d\u044b\u0435 \u043f\u043b\u0430\u0448\u043a\u0438 0/0/0/100 \u0443\u0441\u0438\u043b\u0435\u043d\u044b \u0434\u043e C40 M30 Y30 K100 (~{0:0.#}% / {1:0.#} \u0441\u043c\u00b2)",
                    enhPct, enhCm2));
            }
            else
            {
                double solidPct = report.SolidPureKPixels * 100.0 / report.TotalPixels;
                double solidCm2 = report.SolidPureKAreaMm2 / 100.0;
                bool significant = report.SolidPureKAreaMm2 >= 100.0 || (solidPct >= 0.5 && report.SolidPureKAreaMm2 >= 25.0);

                if (enhanceBlack)
                {
                    notices.Add("[i] \u041a\u0440\u0443\u043f\u043d\u044b\u0445 \u043f\u043b\u0430\u0448\u0435\u043a 0/0/0/100 \u043d\u0435 \u043d\u0430\u0439\u0434\u0435\u043d\u043e \u2014 \u0443\u0441\u0438\u043b\u0435\u043d\u0438\u0435 \u043d\u0435 \u0442\u0440\u0435\u0431\u043e\u0432\u0430\u043b\u043e\u0441\u044c");
                }
                else if (significant)
                {
                    notices.Add(string.Format(
                        CultureInfo.InvariantCulture,
                        "[!] \u041d\u0430\u0439\u0434\u0435\u043d\u0430 \u0447\u0451\u0440\u043d\u0430\u044f \u043f\u043b\u0430\u0448\u043a\u0430 0/0/0/100 (~{0:0.#}% / {1:0.#} \u0441\u043c\u00b2) \u2014 \u043c\u043e\u0436\u0435\u0442 \u043f\u0435\u0447\u0430\u0442\u0430\u0442\u044c\u0441\u044f \u0431\u043b\u0435\u0434\u043d\u043e (\u043c\u043e\u0436\u043d\u043e \u0432\u043a\u043b. \u0443\u0441\u0438\u043b\u0435\u043d\u0438\u0435)",
                        solidPct, solidCm2));
                }
            }

            return notices;
        }
    }
}
