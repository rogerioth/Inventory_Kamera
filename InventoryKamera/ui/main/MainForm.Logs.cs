using InventoryKamera.ui;
using NLog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace InventoryKamera
{
    public partial class MainForm
    {
        private readonly object logQueueLock = new object();
        private readonly System.Collections.Generic.Queue<UiLogEntry> pendingLogEntries =
            new System.Collections.Generic.Queue<UiLogEntry>();
        private readonly LinkedList<UiLogEntry> displayedLogEntries = new LinkedList<UiLogEntry>();
        private int pendingLogCharacters;
        private int displayedLogCharacters;
        private bool pendingLogReset;
        private bool pendingLogError;
        private bool logPanelDisposed;
        private bool updatingLogFilters;
        private bool logWindowExpanded;
        private int savedLogPanelHeight = 220;
        private IDisposable logSubscription;
        private System.Windows.Forms.Timer logRefreshTimer;
        private SplitContainer logSplit;
        private CheckBox showLogsToggle;
        private CheckBox logInfoFilter;
        private CheckBox logWarningFilter;
        private CheckBox logErrorFilter;
        private CheckBox logAutoScroll;
        private RichTextBox logText;

        private void InitializeLogPanel(SplitContainer split, CheckBox toggle)
        {
            logSplit = split;
            showLogsToggle = toggle;
            split.Panel2Collapsed = true;
            split.FixedPanel = FixedPanel.Panel2;
            split.Panel2MinSize = Math.Max(120, split.Panel2MinSize);

            var group = new GroupBox
            {
                Text = "Logs",
                Dock = DockStyle.Fill,
                Padding = new Padding(10, 6, 10, 10),
                BackColor = Color.FromArgb(240, 240, 240),
                ForeColor = SystemColors.ControlText
            };
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var toolbar = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill,
                WrapContents = true,
                Margin = new Padding(0, 0, 0, 6),
                Padding = Padding.Empty
            };
            toolbar.Controls.Add(new Label
            {
                Text = "Show:",
                AutoSize = true,
                Margin = new Padding(0, 5, 8, 0),
                ForeColor = Color.FromArgb(85, 85, 85)
            });
            logInfoFilter = CreateLogCheckbox("Info", true);
            logWarningFilter = CreateLogCheckbox("Warnings", true);
            logErrorFilter = CreateLogCheckbox("Errors", true);
            logAutoScroll = CreateLogCheckbox("Auto-scroll", true);
            logAutoScroll.Margin = new Padding(16, 4, 10, 0);
            toolbar.Controls.Add(logInfoFilter);
            toolbar.Controls.Add(logWarningFilter);
            toolbar.Controls.Add(logErrorFilter);
            toolbar.Controls.Add(logAutoScroll);
            toolbar.Controls.Add(CreateLogButton("Copy", CopyVisibleLogs));
            toolbar.Controls.Add(CreateLogButton("Clear", (sender, e) => ResetScanLogs()));
            toolbar.Controls.Add(CreateLogButton("Open log folder", OpenLogFolder));

            logText = new RichTextBox
            {
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                BackColor = Color.White,
                ForeColor = Color.Black,
                Font = new Font("Consolas", 9F),
                ReadOnly = true,
                DetectUrls = false,
                WordWrap = false,
                ScrollBars = RichTextBoxScrollBars.Both,
                HideSelection = false,
                BorderStyle = BorderStyle.FixedSingle,
                AccessibleName = "Scan logs"
            };
            layout.Controls.Add(toolbar, 0, 0);
            layout.Controls.Add(logText, 0, 1);
            group.Controls.Add(layout);
            split.Panel2.Controls.Add(group);

            logInfoFilter.CheckedChanged += LogFilterChanged;
            logWarningFilter.CheckedChanged += LogFilterChanged;
            logErrorFilter.CheckedChanged += LogFilterChanged;
            logAutoScroll.CheckedChanged += (sender, e) =>
            {
                if (logAutoScroll.Checked)
                {
                    logText.Select(logText.TextLength, 0);
                    logText.ScrollToCaret();
                }
            };
            toggle.CheckedChanged += ShowLogsChanged;
            split.SplitterMoved += (sender, e) =>
            {
                if (!split.Panel2Collapsed)
                    savedLogPanelHeight = split.Panel2.Height;
            };

            // Subscription replay and live delivery share one ordered queue.
            // Logging threads never touch controls or post one message per line.
            logSubscription = UiLogTarget.Subscribe(QueueLogEntry);
            logRefreshTimer = new System.Windows.Forms.Timer(components) { Interval = 150 };
            logRefreshTimer.Tick += (sender, e) => DrainLogQueue();
            logRefreshTimer.Start();
            DrainLogQueue();
        }

        private static CheckBox CreateLogCheckbox(string text, bool isChecked)
        {
            return new CheckBox
            {
                Text = text,
                AutoSize = true,
                Checked = isChecked,
                Margin = new Padding(0, 4, 10, 0),
                UseVisualStyleBackColor = true
            };
        }

        private static Button CreateLogButton(string text, EventHandler click)
        {
            var button = new Button
            {
                Text = text,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(60, 26),
                Padding = new Padding(6, 0, 6, 0),
                Margin = new Padding(0, 0, 6, 0),
                UseVisualStyleBackColor = true
            };
            button.Click += click;
            return button;
        }

        private void QueueLogEntry(UiLogEntry entry)
        {
            lock (logQueueLock)
            {
                if (logPanelDisposed)
                    return;
                if (entry.IsReset)
                {
                    pendingLogEntries.Clear();
                    pendingLogCharacters = 0;
                    pendingLogReset = true;
                    pendingLogError = false;
                    return;
                }

                pendingLogEntries.Enqueue(entry);
                pendingLogCharacters += entry.Text.Length;
                pendingLogError |= entry.IsError;
                while (pendingLogEntries.Count > UiLogTarget.MaximumEntryCount ||
                       pendingLogCharacters > UiLogTarget.MaximumTextLength)
                    pendingLogCharacters -= pendingLogEntries.Dequeue().Text.Length;
            }
        }

        private void DrainLogQueue()
        {
            if (logPanelDisposed || logText == null || logText.IsDisposed)
                return;

            UiLogEntry[] entries;
            bool reset;
            bool error;
            lock (logQueueLock)
            {
                if (!pendingLogReset && pendingLogEntries.Count == 0)
                    return;
                entries = pendingLogEntries.ToArray();
                pendingLogEntries.Clear();
                pendingLogCharacters = 0;
                reset = pendingLogReset;
                error = pendingLogError;
                pendingLogReset = false;
                pendingLogError = false;
            }

            if (reset)
            {
                displayedLogEntries.Clear();
                displayedLogCharacters = 0;
            }
            foreach (var entry in entries)
            {
                displayedLogEntries.AddLast(entry);
                displayedLogCharacters += entry.Text.Length;
            }
            while (displayedLogEntries.Count > UiLogTarget.MaximumEntryCount ||
                   displayedLogCharacters > UiLogTarget.MaximumTextLength)
            {
                displayedLogCharacters -= displayedLogEntries.First.Value.Text.Length;
                displayedLogEntries.RemoveFirst();
            }

            if (error)
            {
                updatingLogFilters = true;
                logErrorFilter.Checked = true;
                updatingLogFilters = false;
                showLogsToggle.Checked = true;
            }
            if (!logSplit.Panel2Collapsed || reset)
                RenderLogEntries(error);
        }

        private void ShowLogsChanged(object sender, EventArgs e)
        {
            if (showLogsToggle.Checked)
            {
                if (!logWindowExpanded && WindowState == FormWindowState.Normal)
                {
                    var workArea = Screen.FromControl(this).WorkingArea;
                    Height += Math.Max(0, Math.Min(LayoutPixels(210), workArea.Bottom - Bottom));
                    logWindowExpanded = true;
                }
                int requestedHeight = savedLogPanelHeight;
                logSplit.Panel2Collapsed = false;
                int available = logSplit.Height - logSplit.SplitterWidth - logSplit.Panel1MinSize;
                int panelHeight = Math.Min(Math.Max(logSplit.Panel2MinSize, requestedHeight), available);
                if (panelHeight >= logSplit.Panel2MinSize)
                    logSplit.SplitterDistance = logSplit.Height - logSplit.SplitterWidth - panelHeight;
                RenderLogEntries(false);
            }
            else
            {
                if (!logSplit.Panel2Collapsed)
                    savedLogPanelHeight = logSplit.Panel2.Height;
                logSplit.Panel2Collapsed = true;
            }
        }

        private void LogFilterChanged(object sender, EventArgs e)
        {
            if (!updatingLogFilters)
                RenderLogEntries(false);
        }

        private bool IsLogEntryVisible(UiLogEntry entry)
        {
            if (entry.Level.Ordinal >= LogLevel.Error.Ordinal)
                return logErrorFilter.Checked;
            if (entry.Level.Ordinal >= LogLevel.Warn.Ordinal)
                return logWarningFilter.Checked;
            return logInfoFilter.Checked;
        }

        private void RenderLogEntries(bool revealError)
        {
            if (logPanelDisposed || logText == null || logText.IsDisposed)
                return;

            int selectionStart = logText.SelectionStart;
            int selectionLength = logText.SelectionLength;
            var scrollPosition = new Point();
            LogSendMessage(logText.Handle, 0x04DD, IntPtr.Zero, ref scrollPosition); // EM_GETSCROLLPOS
            var rtf = new StringBuilder("{\\rtf1\\ansi\\deff0{\\fonttbl{\\f0 Consolas;}}" +
                "{\\colortbl;\\red0\\green0\\blue0;\\red138\\green90\\blue0;\\red197\\green15\\blue31;}" +
                "\\f0\\fs18\\pard\\tx480 ");
            int textLength = 0;
            int latestErrorStart = -1;
            foreach (var entry in displayedLogEntries)
            {
                if (!IsLogEntryVisible(entry))
                    continue;
                int color;
                if (entry.IsError) color = 3;
                else color = entry.Level.Ordinal >= LogLevel.Warn.Ordinal ? 2 : 1;
                rtf.Append("\\cf").Append(color).Append(' ');
                string text = entry.Text.Replace("\r\n", "\n").Replace('\r', '\n');
                if (entry.IsError)
                    latestErrorStart = textLength;
                AppendLogRtfText(rtf, text);
                rtf.Append("\\par ");
                textLength += text.Length + 1;
            }
            rtf.Append('}');
            logText.Rtf = rtf.ToString();

            if (revealError && latestErrorStart >= 0)
            {
                logText.Select(Math.Min(latestErrorStart, logText.TextLength), 0);
                logText.ScrollToCaret();
            }
            else if (logAutoScroll.Checked)
            {
                logText.Select(logText.TextLength, 0);
                logText.ScrollToCaret();
            }
            else
            {
                selectionStart = Math.Min(selectionStart, logText.TextLength);
                logText.Select(selectionStart, Math.Min(selectionLength, logText.TextLength - selectionStart));
                LogSendMessage(logText.Handle, 0x04DE, IntPtr.Zero, ref scrollPosition); // EM_SETSCROLLPOS
            }
        }

        private static void AppendLogRtfText(StringBuilder rtf, string text)
        {
            foreach (char character in text)
            {
                switch (character)
                {
                    case '\\': case '{': case '}': rtf.Append('\\').Append(character); break;
                    case '\n': rtf.Append("\\line "); break;
                    case '\t': rtf.Append("\\tab "); break;
                    default:
                        if (character > 127)
                            rtf.Append("\\u").Append((short)character).Append('?');
                        else if (!char.IsControl(character))
                            rtf.Append(character);
                        break;
                }
            }
        }

        private void CopyVisibleLogs(object sender, EventArgs e)
        {
            DrainLogQueue();
            if (logText.TextLength == 0)
                return;
            try { Clipboard.SetText(logText.Text); }
            catch (ExternalException ex) { Logger.Warn(ex, "Could not copy logs to the clipboard"); }
        }

        private void OpenLogFolder(object sender, EventArgs e)
        {
            try
            {
                string directory = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "logging"));
                Directory.CreateDirectory(directory);
                Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
            }
            catch (Exception ex) { Logger.Error(ex, "[FAILURE] Could not open the log folder"); }
        }

        private void ResetScanLogs()
        {
            UiLogTarget.ClearBuffer();
            DrainLogQueue();
        }

        private void DisposeLogPanel()
        {
            lock (logQueueLock)
            {
                logPanelDisposed = true;
                pendingLogEntries.Clear();
                pendingLogCharacters = 0;
            }
            logSubscription?.Dispose();
            logSubscription = null;
            logRefreshTimer?.Stop();
            logRefreshTimer?.Dispose();
            logRefreshTimer = null;
        }

        [DllImport("user32.dll", EntryPoint = "SendMessage")]
        private static extern IntPtr LogSendMessage(IntPtr handle, int message, IntPtr wParam, ref Point lParam);
    }
}
