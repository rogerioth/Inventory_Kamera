using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace InventoryKamera.ui
{
    public partial class ExecutablesForm : Form
    {
        public ExecutablesForm()
        {
            InitializeComponent();
            InitializeResponsiveLayout();
        }

        private void InitializeResponsiveLayout()
        {
            SuspendLayout();
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            Font = new Font("Segoe UI", 9F);
            BackColor = Color.FromArgb(240, 240, 240);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            SizeGripStyle = SizeGripStyle.Auto;
            MinimumSize = new Size(500, 340);
            ClientSize = new Size(640, 440);
            Padding = new Padding(12);

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 2,
                Margin = Padding.Empty,
                TabIndex = 0,
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            ConfigureExecutableColumn(panel1, ProcessLabel, ProcessListBox, AddButton);
            ConfigureExecutableColumn(panel2, ExecutableLabel, ExecutableListBox, RemoveButton);
            panel1.Margin = new Padding(0, 0, 6, 0);
            panel1.TabIndex = 0;
            panel2.Margin = new Padding(6, 0, 0, 0);
            panel2.TabIndex = 1;
            layout.Controls.Add(panel1, 0, 0);
            layout.Controls.Add(panel2, 1, 0);

            var footer = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Margin = new Padding(0, 12, 0, 0),
                TabIndex = 2,
            };
            DoneButton.Dock = DockStyle.None;
            DoneButton.Margin = Padding.Empty;
            DoneButton.Size = new Size(90, 29);
            DoneButton.TabIndex = 0;
            footer.Controls.Add(DoneButton);
            layout.Controls.Add(footer, 0, 1);
            layout.SetColumnSpan(footer, 2);

            Controls.Add(layout);
            ResumeLayout(true);
        }

        private static void ConfigureExecutableColumn(Panel panel, Label heading, ListBox list, Button action)
        {
            panel.SuspendLayout();
            panel.Dock = DockStyle.Fill;
            panel.Padding = Padding.Empty;

            var column = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Margin = Padding.Empty,
                TabIndex = 0,
            };
            column.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            column.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            column.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            column.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            heading.Dock = DockStyle.Fill;
            heading.Margin = new Padding(0, 0, 0, 8);
            heading.TabIndex = 0;
            list.Dock = DockStyle.Fill;
            list.IntegralHeight = false;
            list.Margin = new Padding(0, 0, 0, 8);
            list.TabIndex = 1;
            action.Dock = DockStyle.Fill;
            action.Margin = Padding.Empty;
            action.MinimumSize = new Size(120, 29);
            action.TabIndex = 2;
            column.Controls.Add(heading, 0, 0);
            column.Controls.Add(list, 0, 1);
            column.Controls.Add(action, 0, 2);
            panel.Controls.Add(column);
            panel.ResumeLayout(true);
        }

        private void ExecutablesForm_Load(object sender, EventArgs e)
        {
            PopulateExecutables();
            PopulateProcesses();

        }

        private void PopulateProcesses()
        {
            var processes = new HashSet<string>();

            Process.GetProcesses().ToList().ForEach(p => processes.Add(p.ProcessName));

            ProcessListBox.Items.Clear();
            foreach (var process in processes)
            {
                if (Properties.Settings.Default.Executables.Contains(process)) continue;

                ProcessListBox.Items.Add(process);
            }
        }

        private void PopulateExecutables()
        {
            ExecutableListBox.Items.Clear();
            foreach (var executable in Properties.Settings.Default.Executables)
            {
                ExecutableListBox.Items.Add(executable);
            }
        }

        private void DoneButton_Click(object sender, EventArgs e)
        {
            this.Close();
        }

        private void AddButton_Click(object sender, EventArgs e)
        {
            string selected = (string) ProcessListBox.SelectedItem;
            if (selected != null) 
            { 
                if (!Properties.Settings.Default.Executables.Contains(selected))
                {
                    Properties.Settings.Default.Executables.Add(selected);
                    PopulateExecutables();
                    PopulateProcesses();
                }
            }
        }

        private void RemoveButton_Click(object sender, EventArgs e)
        {
            string selected = (string) ExecutableListBox.SelectedItem;
            if (selected != null) 
            {
                Properties.Settings.Default.Executables.Remove(selected);
                PopulateExecutables();
                PopulateProcesses();
            }
        }
    }
}
