using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ResourceChecker
{
    public sealed class MainForm : Form
    {
        readonly TextBox _pathBox = new()
        {
            Dock = DockStyle.Fill,
            PlaceholderText = "Client root — the folder containing Client.exe, Data\\, Map\\, Sound\\",
        };
        readonly Button _browseBtn = new() { Text = "Browse…", AutoSize = true };
        readonly Button _autoDetectBtn = new() { Text = "Auto-detect", AutoSize = true };
        readonly Button _runBtn = new() { Text = "Check", AutoSize = true };
        readonly Button _exportBtn = new() { Text = "Export report…", AutoSize = true, Enabled = false };
        readonly ListView _list = new()
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = false,
            UseCompatibleStateImageBehavior = false,
        };
        readonly Label _summary = new()
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(6),
            Text = "Ready.",
        };
        readonly ToolStripStatusLabel _progress = new() { Text = "Idle" };

        public MainForm()
        {
            Text = "Mir 2 Client Resource Checker";
            Width = 1000;
            Height = 640;
            MinimumSize = new Size(720, 420);
            StartPosition = FormStartPosition.CenterScreen;

            _list.Columns.Add("Level", 70);
            _list.Columns.Add("Section", 120);
            _list.Columns.Add("Message", 520);
            _list.Columns.Add("How to fix", 260);

            var topRow = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 40,
                ColumnCount = 5,
                Padding = new Padding(6),
            };
            topRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            topRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            topRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            topRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            topRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            topRow.Controls.Add(_pathBox, 0, 0);
            topRow.Controls.Add(_browseBtn, 1, 0);
            topRow.Controls.Add(_autoDetectBtn, 2, 0);
            topRow.Controls.Add(_runBtn, 3, 0);
            topRow.Controls.Add(_exportBtn, 4, 0);

            var summaryBar = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 44,
                BackColor = SystemColors.Info,
            };
            summaryBar.Controls.Add(_summary);

            var status = new StatusStrip();
            status.Items.Add(_progress);

            Controls.Add(_list);
            Controls.Add(summaryBar);
            Controls.Add(topRow);
            Controls.Add(status);

            _browseBtn.Click += OnBrowse;
            _autoDetectBtn.Click += OnAutoDetect;
            _runBtn.Click += OnRun;
            _exportBtn.Click += OnExport;
        }

        void OnBrowse(object sender, EventArgs e)
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "Pick the Client root (folder that holds Data, Map, Sound)",
                UseDescriptionForTitle = true,
            };
            if (dlg.ShowDialog(this) == DialogResult.OK)
                _pathBox.Text = dlg.SelectedPath;
        }

        // Guess where the client lives relative to this exe. When bundled into
        // Build\Server Tools\ResourceChecker\Debug\, the client usually sits in
        // a sibling ..\..\..\Client\Debug\ (or wherever the user placed it).
        // We just try a couple of common locations rather than being clever.
        void OnAutoDetect(object sender, EventArgs e)
        {
            var here = AppContext.BaseDirectory;
            string[] candidates =
            {
                Path.Combine(here, "..", "..", "Client", "Debug"),
                Path.Combine(here, "..", "..", "..", "Client"),
                Path.Combine(here, "..", "..", "..", "Client", "Debug"),
                Path.Combine(here, ".."),
            };
            foreach (var c in candidates)
            {
                var full = Path.GetFullPath(c);
                if (Directory.Exists(Path.Combine(full, "Data"))
                    && Directory.Exists(Path.Combine(full, "Map"))
                    && Directory.Exists(Path.Combine(full, "Sound")))
                {
                    _pathBox.Text = full;
                    return;
                }
            }
            MessageBox.Show(this,
                "Couldn't auto-detect a client folder near this exe. Point Browse… at it manually.",
                "Auto-detect", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        async void OnRun(object sender, EventArgs e)
        {
            var root = _pathBox.Text?.Trim();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                MessageBox.Show(this, "Pick a client root folder first.", "Nothing to check",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            SetBusy(true);
            _list.Items.Clear();
            _summary.Text = "Running…";

            ResourceChecks.Report report;
            try
            {
                report = await Task.Run(() => ResourceChecks.Run(root, msg => Invoke(() => _progress.Text = msg)));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.ToString(), "Check crashed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                SetBusy(false);
                return;
            }

            RenderReport(report);
            SetBusy(false);
            _exportBtn.Enabled = true;
            _progress.Text = "Done.";
        }

        void RenderReport(ResourceChecks.Report r)
        {
            _list.BeginUpdate();
            foreach (var f in r.Findings)
            {
                var item = new ListViewItem(new[]
                {
                    LevelText(f.Level),
                    f.Section ?? "",
                    f.Message ?? "",
                    f.Hint ?? "",
                });
                item.ForeColor = LevelColor(f.Level);
                _list.Items.Add(item);
            }
            _list.EndUpdate();

            var sb = new StringBuilder();
            if (r.IsFatal)
                sb.Append("✗ Client will NOT start. ");
            else if (r.CoreWrongVersion > 0)
                sb.Append("⚠ Client may start but some libs need re-saving. ");
            else
                sb.Append("✓ Core dependencies present. ");

            sb.Append($"Core missing: {r.CoreMissing}  ·  Wrong version: {r.CoreWrongVersion}  ·  Dirs missing: {r.DirectoriesMissing}");

            if (r.ShandaComplete && !r.WemadeComplete)
                sb.Append("  ·  Shanda-style ('仿盛大') tiles: complete. Wemade tiles incomplete.");
            else if (r.WemadeComplete && !r.ShandaComplete)
                sb.Append("  ·  Wemade tiles complete. Shanda-style ('仿盛大') tiles incomplete.");
            else if (r.WemadeComplete && r.ShandaComplete)
                sb.Append("  ·  All Mir 2 tile groups complete.");

            _summary.Text = sb.ToString();
            _summary.ForeColor = r.IsFatal ? Color.DarkRed
                : (r.CoreWrongVersion > 0 ? Color.DarkOrange : Color.DarkGreen);
        }

        static string LevelText(ResourceChecks.Severity s) => s switch
        {
            ResourceChecks.Severity.Ok   => "OK",
            ResourceChecks.Severity.Warn => "WARN",
            ResourceChecks.Severity.Fail => "FAIL",
            _                            => "INFO",
        };

        static Color LevelColor(ResourceChecks.Severity s) => s switch
        {
            ResourceChecks.Severity.Ok   => Color.DarkGreen,
            ResourceChecks.Severity.Warn => Color.DarkOrange,
            ResourceChecks.Severity.Fail => Color.DarkRed,
            _                            => Color.DimGray,
        };

        void OnExport(object sender, EventArgs e)
        {
            using var dlg = new SaveFileDialog
            {
                Filter = "Text report (*.txt)|*.txt|CSV (*.csv)|*.csv",
                FileName = $"resource-check-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            var isCsv = dlg.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
            using var w = new StreamWriter(dlg.FileName, false, new UTF8Encoding(true));

            if (isCsv)
            {
                w.WriteLine("Level,Section,Message,Fix");
                foreach (ListViewItem it in _list.Items)
                {
                    w.WriteLine(string.Join(",", it.SubItems.Cast<ListViewItem.ListViewSubItem>()
                        .Select(s => "\"" + (s.Text ?? "").Replace("\"", "\"\"") + "\"")));
                }
            }
            else
            {
                w.WriteLine("Mir 2 Client Resource Check");
                w.WriteLine($"Root: {_pathBox.Text}");
                w.WriteLine($"When: {DateTime.Now:u}");
                w.WriteLine(new string('-', 60));
                foreach (ListViewItem it in _list.Items)
                {
                    w.WriteLine($"[{it.SubItems[0].Text,-4}] {it.SubItems[1].Text,-10}  {it.SubItems[2].Text}");
                    if (!string.IsNullOrWhiteSpace(it.SubItems[3].Text))
                        w.WriteLine($"       fix: {it.SubItems[3].Text}");
                }
                w.WriteLine(new string('-', 60));
                w.WriteLine(_summary.Text);
            }
        }

        void SetBusy(bool busy)
        {
            _runBtn.Enabled = !busy;
            _browseBtn.Enabled = !busy;
            _autoDetectBtn.Enabled = !busy;
            _pathBox.Enabled = !busy;
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        }
    }
}
