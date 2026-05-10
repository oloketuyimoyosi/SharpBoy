using System.Text;

namespace GameboyTest.Form_Design
{
    public class MemoryDebugForm : Form
    {
        private Cartridge currentCartridge;

        // UI Controls
        private RadioButton rdoRom;
        private RadioButton rdoRam;
        private ComboBox bankSelector;
        private TextBox hexViewer;

        public MemoryDebugForm(Cartridge cartridge)
        {
            this.currentCartridge = cartridge;

            this.Text = "Memory Bank Debugger";
            this.Size = new Size(600, 500);
            this.StartPosition = FormStartPosition.CenterScreen;

            InitializeUI();
            LoadBankList(); // Load the default ROM banks on startup
        }

        private void InitializeUI()
        {
            // --- TOP PANEL (Controls) ---
            Panel topPanel = new Panel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(10) };

            rdoRom = new RadioButton { Text = "ROM Banks", Checked = true, Width = 100, Location = new Point(10, 10) };
            rdoRam = new RadioButton { Text = "RAM Banks", Width = 100, Location = new Point(110, 10) };

            rdoRom.CheckedChanged += (s, e) => { if (rdoRom.Checked) LoadBankList(); };
            rdoRam.CheckedChanged += (s, e) => { if (rdoRam.Checked) LoadBankList(); };

            Label lblBank = new Label { Text = "Select Bank:", Width = 75, Location = new Point(220, 12) };

            bankSelector = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80, Location = new Point(300, 10) };
            bankSelector.SelectedIndexChanged += BankSelector_SelectedIndexChanged;

            topPanel.Controls.Add(rdoRom);
            topPanel.Controls.Add(rdoRam);
            topPanel.Controls.Add(lblBank);
            topPanel.Controls.Add(bankSelector);
            this.Controls.Add(topPanel);
            // --- BOTTOM PANEL (Hex Viewer) ---
            hexViewer = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                ReadOnly = true,
                Font = new Font("Consolas", 10f),
                BackColor = Color.White
            };

            this.Controls.Add(hexViewer);

            // ADD THIS LINE HERE:
            hexViewer.BringToFront();
        }

        private void LoadBankList()
        {
            bankSelector.Items.Clear();

            if (rdoRom.Checked && currentCartridge.RomBanks != null)
            {
                for (int i = 0; i < currentCartridge.TotalRomBanks; i++)
                {
                    bankSelector.Items.Add($"Bank {i:X2}");
                }
            }
            else if (rdoRam.Checked && currentCartridge.RamBanks != null)
            {
                for (int i = 0; i < currentCartridge.TotalRamBanks; i++)
                {
                    bankSelector.Items.Add($"Bank {i:X2}");
                }
            }

            if (bankSelector.Items.Count > 0)
            {
                bankSelector.SelectedIndex = 0; // This triggers the rendering automatically
            }
            else
            {
                hexViewer.Text = "No data available in this region.";
            }
        }

        private void BankSelector_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (bankSelector.SelectedIndex < 0) return;

            int selectedBank = bankSelector.SelectedIndex;
            byte[] activeData = null;

            if (rdoRom.Checked)
            {
                activeData = currentCartridge.RomBanks[selectedBank];
            }
            else if (rdoRam.Checked)
            {
                activeData = currentCartridge.RamBanks[selectedBank];
            }

            if (activeData != null)
            {
                RenderHexDump(activeData);
            }
        }

        private void RenderHexDump(byte[] data)
        {
            // Use StringBuilder because appending 16,000 strings manually will freeze the app
            StringBuilder sb = new StringBuilder(data.Length * 4);

            // Format 16 bytes per line
            for (int i = 0; i < data.Length; i += 16)
            {
                // Print the memory address offset at the start of the line (e.g., "0010: ")
                sb.Append($"{i:X4}: ");

                // Print the 16 bytes
                for (int j = 0; j < 16; j++)
                {
                    if (i + j < data.Length)
                        sb.Append($"{data[i + j]:X2} ");
                }
                sb.AppendLine();
            }

            // Push the giant string to the textbox all at once
            hexViewer.Text = sb.ToString();
        }
    }
}
