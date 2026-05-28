using GameboyTest.Form_Design;
using GameboyTest.MBC;
using GameboyTest.NewFolder;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using System.Diagnostics;
using System.Runtime.InteropServices;
using AForge.Video;
using AForge.Video.DirectShow;
namespace GameboyTest
{
    public partial class Form1 : Form
    {
        private FilterInfoCollection videoDevices;
        private VideoCaptureDevice videoSource;
        private SKControl skiaControl;
        private SKBitmap frameBuffer;
        MemoryBus bus;
        CPU cpu;
        private bool isRunning = false;
        private System.Threading.Tasks.Task emulatorTask;
        private bool useLcdFilter = false;
        // Standard DMG executes ~70224 T-Cycles per frame (4.19 MHz / 59.73 Hz)
        private const int CYCLES_PER_FRAME_DMG = 70224;

        // GBC Double Speed executes exactly twice as many cycles per frame
        private const int CYCLES_PER_FRAME_GBC = 140448;

        public Mbc5Camera gbCameraMapper;
        // --- NEW: LCD Mask Caching ---
        private SKBitmap cachedLcdMask = null;
        private SKRect lastMaskRect = SKRect.Empty;
        public Form1()
        {
            InitializeComponent();
            this.Text = "Game Boy Emulator";
            this.ClientSize = new Size(480, 432);
            this.KeyPreview = true;
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            // 1. Initialize UI Elements (Order matters here for docking)
            InitializeMenu();
            InitializeSkiaControl();

            // 2. Load the default blank screen
            //GenerateTestPicture();
        }

        // --- UI INITIALIZATION ---
        private Cartridge activeCartridge;
        private void InitializeMenu()
        {
            MenuStrip menuStrip = new MenuStrip();

            // --- FILE MENU ---
            ToolStripMenuItem fileMenu = new ToolStripMenuItem("File");

            ToolStripMenuItem loadRomItem = new ToolStripMenuItem("Load ROM...");
            loadRomItem.Click += LoadRom_Click;

            ToolStripMenuItem exitItem = new ToolStripMenuItem("Exit");
            exitItem.Click += (s, ev) => this.Close();

            // Add items to the File dropdown
            fileMenu.DropDownItems.Add(loadRomItem);
            fileMenu.DropDownItems.Add(new ToolStripSeparator());
            fileMenu.DropDownItems.Add(exitItem);

            // --- DEBUG MENU ---
            ToolStripMenuItem debugMenu = new ToolStripMenuItem("Debug");
            ToolStripMenuItem viewVramItem = new ToolStripMenuItem("View VRAM...");
            viewVramItem.Click += ViewVram_Click;
            debugMenu.DropDownItems.Add(viewVramItem);

            ToolStripMenuItem viewGraphicsItem = new ToolStripMenuItem("Graphics Debugger...");
            viewGraphicsItem.Click += OpenGraphicsDebugger_Click;

            debugMenu.DropDownItems.Add(viewGraphicsItem);
            // --- NEW: Diagnostic Suite Item ---

            // Add items to Debug dropdown
            // Added to the dropdown here

            // Add both main menus to the top bar
            menuStrip.Items.Add(fileMenu);
            menuStrip.Items.Add(debugMenu);


            // --- NEW: Add the Options Menu for the Filter ---
            ToolStripMenuItem optionsMenu = new ToolStripMenuItem("Options");
            ToolStripMenuItem lcdFilterItem = new ToolStripMenuItem("LCD Screen Filter");
            lcdFilterItem.CheckOnClick = true; // Makes it act like a checkbox
            lcdFilterItem.Checked = false;     // Default to OFF (Crisp pixels)
            lcdFilterItem.CheckedChanged += (s, ev) =>
            {
                useLcdFilter = lcdFilterItem.Checked;
            };
            optionsMenu.DropDownItems.Add(lcdFilterItem);

            // Add all main menus to the top bar

            menuStrip.Items.Add(optionsMenu);

            // Attach the menu to the window
            this.MainMenuStrip = menuStrip;
            this.Controls.Add(menuStrip);
        }

        private void InitializeSkiaControl()
        {
            skiaControl = new SKControl();
            skiaControl.Dock = DockStyle.Fill; // This will fill the space UNDER the MenuStrip
            skiaControl.PaintSurface += SkiaControl_PaintSurface;
            this.Controls.Add(skiaControl);

            // Bring to front ensures it doesn't accidentally cover the menu
            skiaControl.BringToFront();
        }

        // --- EVENT HANDLERS ---
        // Use Arrow Keys for D-Pad, Z for A, X for B, Enter for Start, Shift for Select
        // 1. THIS REPLACES OnKeyDown! It stops Windows from stealing the arrow keys.
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (bus?.joypad == null) return base.ProcessCmdKey(ref msg, keyData);

            bool handled = true;
            switch (keyData)
            {
                case Keys.Right: bus.joypad.PressButton(JoypadButton.Right); break;
                case Keys.Left: bus.joypad.PressButton(JoypadButton.Left); break;
                case Keys.Up: bus.joypad.PressButton(JoypadButton.Up); break;
                case Keys.Down: bus.joypad.PressButton(JoypadButton.Down); break;
                case Keys.Z: bus.joypad.PressButton(JoypadButton.A); break;
                case Keys.X: bus.joypad.PressButton(JoypadButton.B); break;
                case Keys.Enter: bus.joypad.PressButton(JoypadButton.Start); break;
                case Keys.Shift:
                case Keys.Space:
                    bus.joypad.PressButton(JoypadButton.Select); break;
                default:
                    handled = false; // Not a Game Boy button, let Windows handle it
                    break;
            }

            // If we handled it, return true to KILL the key press so the MenuStrip ignores it!
            if (handled) return true;

            return base.ProcessCmdKey(ref msg, keyData);
        }

        // 2. KEEP your OnKeyUp exactly as it is! Windows does not steal KeyUp events.
        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (bus?.joypad == null) return;

            switch (e.KeyCode)
            {
                case Keys.Right: bus.joypad.ReleaseButton(JoypadButton.Right); break;
                case Keys.Left: bus.joypad.ReleaseButton(JoypadButton.Left); break;
                case Keys.Up: bus.joypad.ReleaseButton(JoypadButton.Up); break;
                case Keys.Down: bus.joypad.ReleaseButton(JoypadButton.Down); break;
                case Keys.Z: bus.joypad.ReleaseButton(JoypadButton.A); break;
                case Keys.X: bus.joypad.ReleaseButton(JoypadButton.B); break;
                case Keys.Enter: bus.joypad.ReleaseButton(JoypadButton.Start); break;
                case Keys.Shift:
                case Keys.Space:
                    bus.joypad.ReleaseButton(JoypadButton.Select); break;
            }
        }
        private void ViewMemory_Click(object sender, EventArgs e)
        {
            // Prevent opening the debugger if no game is loaded
            if (activeCartridge == null || !activeCartridge.IsLoaded)
            {
                MessageBox.Show("Please load a ROM first!", "Debugger Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Create and show the new Debug window
            MemoryDebugForm debugForm = new MemoryDebugForm(activeCartridge);
            debugForm.Show(); // .Show() lets you keep using the main emulator while the debug window is open!
        }
        private void LoadRom_Click(object sender, EventArgs e)
        {

            // Open a standard Windows file browser
            using (OpenFileDialog openFileDialog = new OpenFileDialog())
            {
                openFileDialog.Filter = "Game Boy ROMs (*.gb;*.gbc)|*.gb;*.gbc|All files (*.*)|*.*";
                openFileDialog.Title = "Select a Game Boy ROM";

                if (openFileDialog.ShowDialog() == DialogResult.OK)
                {
                    // USE THE CLASS-LEVEL CARTRIDGE HERE
                    activeCartridge = new Cartridge();

                    string romPath = openFileDialog.FileName;
                    // Pass the selected file path to our Cartridge class
                    if (activeCartridge.LoadRom(openFileDialog.FileName))
                    {
                        // Update the window title to show the loaded game
                        this.Text = $"Game Boy Emulator - Running: {activeCartridge.Title}";
                        
                        // 1. Figure out which MBC chip to create based on the parsed header
                        IMbc activeMbc;
                        if (activeCartridge.MbcType == "None")
                        {
                            activeMbc = new Mbc0(activeCartridge.RomBanks);
                        }
                        else if (activeCartridge.MbcType == "MBC1") // Add this block!
                        {
                            activeMbc = new Mbc1(activeCartridge.RomBanks, activeCartridge.RamBanks);
                        }
                        else if (activeCartridge.MbcType == "MBC3")
                        {
                            activeMbc = new Mbc3(activeCartridge.RomBanks, activeCartridge.RamBanks);
                        }
                        else if (activeCartridge.MbcType == "MBC5")
                        {
                            activeMbc = new Mbc5(activeCartridge.RomBanks, activeCartridge.RamBanks);
                        }
                        else
                        {
                            MessageBox.Show($"Chip {activeCartridge.MbcType} is not implemented yet!");
                            return; // Stop loading if we don't support the chip
                        }

                        // 2. Create the bus and hand it the newly created MBC chip


                        if (activeCartridge.LoadRom(openFileDialog.FileName))
                        {
                            // TRIPWIRE 3: Did the ROM actually load into memory successfully?
                            MessageBox.Show("3. ROM Loaded successfully!");

                            // ... your MBC routing logic ...


                            bool runAsGbc = activeCartridge.ColorMode == GbcMode.CgbSupported || activeCartridge.ColorMode == GbcMode.CgbExclusive;
                            bus = new MemoryBus(activeMbc, OnFrameReadyToDraw);
                            if (runAsGbc)
                            {
                                bus.ppu.IsGbc = true;
                                bus.apu.IsGbc = true;
                                
                            }
                            // Pass it to your CPU (you'll need to update your CPU constructor to accept/pass this down)
                            cpu = new CPU(bus, runAsGbc);
                            cpu.AF = (ushort)((runAsGbc ? 0x1100 : 0x0100) | (cpu.AF & 0x00FF));
                            APU testApu = new APU();
                            if (activeCartridge.HardwareFeature == "Camera")
                            {
                                gbCameraMapper = new Mbc5Camera();
                                bus.AttachCameraMapper(gbCameraMapper);
                                StartWebcam();
                            }
                            else
                            {
                                bus.DetachCameraMapper();
                            }
                            // START THE LOGGER HERE!
                            string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cpu_log.txt");

                            // Optional: provide path to a blargg test text file
                            string blarggValidationPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "blargg9.txt");
                            //cpu.Logger.Start(logPath, blarggValidationPath);

                            // Add a temporary line to write a test string immediately
                            isRunning = false;
                            // ... task thread starting logic ...
                        }

                        // 2. Wait a split second to let the old thread safely die
                        if (emulatorTask != null && !emulatorTask.IsCompleted)
                            emulatorTask.Wait(100);

                        // 3. Turn the power on and boot the new thread!
                        isRunning = true;
                        emulatorTask = System.Threading.Tasks.Task.Run(() => RunEmulatorEngine());
                    }
                    else
                    {
                        MessageBox.Show("Failed to load the ROM file.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }
        private void OpenGraphicsDebugger_Click(object sender, EventArgs e)
        {
            Form debugForm = new Form
            {
                Text = "Game Boy Graphics Debugger",
                ClientSize = new System.Drawing.Size(512 + 200, 512),
                FormBorderStyle = FormBorderStyle.FixedToolWindow,
                StartPosition = FormStartPosition.CenterScreen
            };

            // --- 1. DECLARE CONTROLS FIRST (So our canvases can read them) ---
            CheckBox chkShowGrid = new CheckBox { Text = "Show Grid", Checked = true, Location = new System.Drawing.Point(10, 20) };
            ComboBox cmbZoom = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new System.Drawing.Point(10, 45), Width = 120 };
            cmbZoom.Items.AddRange(new object[] { "1x Zoom", "2x Zoom", "4x Zoom" });
            cmbZoom.SelectedIndex = 1; // Default to 2x

            ComboBox cmbPalette = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new System.Drawing.Point(10, 20), Width = 150 };
            cmbPalette.Items.AddRange(new object[] { "Background (BGP)", "Sprite 0 (OBP0)", "Sprite 1 (OBP1)" });
            cmbPalette.SelectedIndex = 0;

            // NEW: GBC Palette Selector (0-7)
            Label lblGbcPalette = new Label { Text = "GBC Palette (0-7):", Location = new System.Drawing.Point(10, 50), Width = 100 };
            NumericUpDown numGbcPalette = new NumericUpDown { Maximum = 7, Minimum = 0, Value = 0, Location = new System.Drawing.Point(110, 48), Width = 40 };

            CheckBox chkUseMap2 = new CheckBox { Text = "Use Map 2 (0x9C00)", Location = new System.Drawing.Point(10, 20), Width = 150 };

            // NEW: Sprite Overlay Checkbox for the BG Map
            CheckBox chkShowSprites = new CheckBox { Text = "Show Sprites on Map", Location = new System.Drawing.Point(10, 45), Width = 150 };

            Label lblTileData = new Label { Text = "ID: --\nAddr: --", Dock = DockStyle.Top, Height = 40 };

            // --- 2. THE TABS & CANVASES ---
            TabControl tabs = new TabControl { Dock = DockStyle.Fill };
            int GetScale() => cmbZoom.SelectedIndex == 0 ? 1 : (cmbZoom.SelectedIndex == 1 ? 2 : 4);

            // ==========================================
            // TAB 1: VRAM (Expanded for GBC!)
            // ==========================================
            TabPage tabVram = new TabPage("VRAM");
            var vramCanvas = new SkiaSharp.Views.Desktop.SKControl { Dock = DockStyle.Fill };
            vramCanvas.PaintSurface += (s, args) =>
            {
                if (bus?.ppu == null) { args.Surface.Canvas.Clear(SkiaSharp.SKColors.Black); return; }

                // Pass BOTH the DMG palette dropdown and the new GBC palette spinner!
                uint[] pixels = bus.ppu.GetVramTexture(cmbPalette.SelectedIndex, (int)numGbcPalette.Value);

                // Expanded to 256x256 to fit both GBC VRAM banks!
                var info = new SkiaSharp.SKImageInfo(256, 256, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
                using (var bitmap = new SkiaSharp.SKBitmap())
                {
                    unsafe
                    {
                        fixed (uint* ptr = pixels)
                        {
                            bitmap.InstallPixels(info, (IntPtr)ptr, info.RowBytes, delegate { });
                            int scale = GetScale();
                            var destRect = new SkiaSharp.SKRect(0, 0, 256 * scale, 256 * scale);

                            using (var paint = new SkiaSharp.SKPaint { FilterQuality = SkiaSharp.SKFilterQuality.None })
                                args.Surface.Canvas.DrawBitmap(bitmap, destRect, paint);

                            if (chkShowGrid.Checked)
                            {
                                using (var gridPaint = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(255, 255, 255, 75), StrokeWidth = 1 })
                                {
                                    int tileSize = 8 * scale;
                                    for (int x = 0; x <= 256 * scale; x += tileSize) args.Surface.Canvas.DrawLine(x, 0, x, 256 * scale, gridPaint);
                                    for (int y = 0; y <= 256 * scale; y += tileSize) args.Surface.Canvas.DrawLine(0, y, 256 * scale, y, gridPaint);
                                }
                            }
                        }
                    }
                }
            };
            vramCanvas.MouseMove += (s, mouseArgs) =>
            {
                int scale = GetScale();
                int pixelX = mouseArgs.X / scale;
                int pixelY = mouseArgs.Y / scale;

                if (pixelX >= 0 && pixelX < 256 && pixelY >= 0 && pixelY < 256)
                {
                    // Now accounts for 32 tiles across instead of 16!
                    int tileIndex = ((pixelY / 8) * 32) + (pixelX / 8);

                    // If the index is > 511, it's in Bank 1!
                    int bank = tileIndex >= 512 ? 1 : 0;
                    int localIndex = tileIndex % 512;
                    lblTileData.Text = $"Bank {bank} | ID: 0x{localIndex:X2}\nAddr: 0x{(0x8000 + (localIndex * 16)):X4}";
                }
            };
            tabVram.Controls.Add(vramCanvas);

            // ==========================================
            // TAB 2: BG MAP (With Sprite Overlay!)
            // ==========================================
            TabPage tabBg = new TabPage("BG Map");
            var bgCanvas = new SkiaSharp.Views.Desktop.SKControl { Dock = DockStyle.Fill };
            bgCanvas.PaintSurface += (s, args) =>
            {
                if (bus?.ppu == null) { args.Surface.Canvas.Clear(SkiaSharp.SKColors.Black); return; }

                // Pass the new Sprite Overlay checkbox!
                uint[] pixels = bus.ppu.GetBackgroundMapTexture(chkUseMap2.Checked, chkShowSprites.Checked);
                var info = new SkiaSharp.SKImageInfo(256, 256, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
                using (var bitmap = new SkiaSharp.SKBitmap())
                {
                    unsafe
                    {
                        fixed (uint* ptr = pixels)
                        {
                            bitmap.InstallPixels(info, (IntPtr)ptr, info.RowBytes, delegate { });
                            int scale = GetScale();

                            using (var paint = new SkiaSharp.SKPaint { FilterQuality = SkiaSharp.SKFilterQuality.None })
                                args.Surface.Canvas.DrawBitmap(bitmap, new SkiaSharp.SKRect(0, 0, 256 * scale, 256 * scale), paint);

                            if (chkShowGrid.Checked)
                            {
                                using (var gridPaint = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(255, 255, 255, 75), StrokeWidth = 1 })
                                {
                                    int tileSize = 8 * scale;
                                    for (int x = 0; x <= 256 * scale; x += tileSize) args.Surface.Canvas.DrawLine(x, 0, x, 256 * scale, gridPaint);
                                    for (int y = 0; y <= 256 * scale; y += tileSize) args.Surface.Canvas.DrawLine(0, y, 256 * scale, y, gridPaint);
                                }
                            }

                            int scx = bus.ppu.SCX;
                            int scy = bus.ppu.SCY;
                            using (var viewPaint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.Red, Style = SkiaSharp.SKPaintStyle.Stroke, StrokeWidth = 2 * scale })
                            {
                                for (int wrapX = 0; wrapX < 2; wrapX++)
                                {
                                    for (int wrapY = 0; wrapY < 2; wrapY++)
                                    {
                                        int drawX = (scx - (wrapX * 256)) * scale;
                                        int drawY = (scy - (wrapY * 256)) * scale;
                                        args.Surface.Canvas.DrawRect(drawX, drawY, 160 * scale, 144 * scale, viewPaint);
                                    }
                                }
                            }
                        }
                    }
                }
            };
            tabBg.Controls.Add(bgCanvas);

            // ==========================================
            // TAB 3: OAM (SPRITES)
            // ==========================================
            TabPage tabOam = new TabPage("Sprites (OAM)");
            var oamCanvas = new SkiaSharp.Views.Desktop.SKControl { Dock = DockStyle.Fill };
            oamCanvas.PaintSurface += (s, args) =>
            {
                if (bus?.ppu == null) { args.Surface.Canvas.Clear(SkiaSharp.SKColors.Black); return; }

                uint[] pixels = bus.ppu.GetOamTexture();

                // Sprite viewer is 64x80 pixels internal resolution
                var info = new SkiaSharp.SKImageInfo(64, 80, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
                using (var bitmap = new SkiaSharp.SKBitmap())
                {
                    unsafe
                    {
                        fixed (uint* ptr = pixels)
                        {
                            bitmap.InstallPixels(info, (IntPtr)ptr, info.RowBytes, delegate { });
                            int scale = GetScale();
                            var destRect = new SkiaSharp.SKRect(0, 0, 64 * scale, 80 * scale);

                            using (var paint = new SkiaSharp.SKPaint { FilterQuality = SkiaSharp.SKFilterQuality.None })
                                args.Surface.Canvas.DrawBitmap(bitmap, destRect, paint);

                            if (chkShowGrid.Checked)
                            {
                                using (var gridPaint = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(255, 255, 255, 75), StrokeWidth = 1 })
                                {
                                    int tileWidth = 8 * scale;
                                    int tileHeight = ((bus.ppu.LCDC & 0x04) != 0 ? 16 : 8) * scale;

                                    for (int x = 0; x <= 64 * scale; x += tileWidth) args.Surface.Canvas.DrawLine(x, 0, x, 80 * scale, gridPaint);
                                    for (int y = 0; y <= 80 * scale; y += tileHeight) args.Surface.Canvas.DrawLine(0, y, 64 * scale, y, gridPaint);
                                }
                            }
                        }
                    }
                }
            };
            tabOam.Controls.Add(oamCanvas);

            // Add all 3 tabs to the window
            tabs.Controls.Add(tabVram);
            tabs.Controls.Add(tabBg);
            tabs.Controls.Add(tabOam);

            // --- 3. WIRE UP UI EVENTS (Instant Redraws) ---
            cmbZoom.SelectedIndexChanged += (s, ev) => { vramCanvas.Invalidate(); bgCanvas.Invalidate(); oamCanvas.Invalidate(); };
            cmbPalette.SelectedIndexChanged += (s, ev) => vramCanvas.Invalidate();
            numGbcPalette.ValueChanged += (s, ev) => vramCanvas.Invalidate(); // Updates VRAM when spinner changes
            chkShowGrid.CheckedChanged += (s, ev) => { vramCanvas.Invalidate(); bgCanvas.Invalidate(); oamCanvas.Invalidate(); };
            chkUseMap2.CheckedChanged += (s, ev) => bgCanvas.Invalidate();
            chkShowSprites.CheckedChanged += (s, ev) => bgCanvas.Invalidate(); // Updates Map when overlay is toggled

            // --- 4. ASSEMBLE THE RIGHT PANEL ---
            Panel optionsPanel = new Panel { Dock = DockStyle.Right, Width = 200, Padding = new Padding(10), BackColor = System.Drawing.Color.WhiteSmoke };

            GroupBox grpMaps = new GroupBox { Text = "Background Maps", Dock = DockStyle.Top, Height = 75 };
            grpMaps.Controls.Add(chkUseMap2);
            grpMaps.Controls.Add(chkShowSprites); // Added Sprite Overlay option

            GroupBox grpPalette = new GroupBox { Text = "Render Palette", Dock = DockStyle.Top, Height = 80 };
            grpPalette.Controls.Add(cmbPalette);
            grpPalette.Controls.Add(lblGbcPalette); // Added GBC UI
            grpPalette.Controls.Add(numGbcPalette); // Added GBC UI

            GroupBox grpDisplay = new GroupBox { Text = "Display", Dock = DockStyle.Top, Height = 80 };
            grpDisplay.Controls.Add(chkShowGrid);
            grpDisplay.Controls.Add(cmbZoom);

            Label lblHoverTitle = new Label { Text = "Tile Inspector:", Font = new System.Drawing.Font("Segoe UI", 9, System.Drawing.FontStyle.Bold), Dock = DockStyle.Top, Height = 25 };

            // Add in reverse order for DockStyle.Top
            optionsPanel.Controls.Add(grpMaps);
            optionsPanel.Controls.Add(grpPalette);
            optionsPanel.Controls.Add(grpDisplay);
            optionsPanel.Controls.Add(lblTileData);
            optionsPanel.Controls.Add(lblHoverTitle);

            debugForm.Controls.Add(optionsPanel);
            debugForm.Controls.Add(tabs);

            // --- 5. TIMER LOOP ---
            System.Windows.Forms.Timer refreshTimer = new System.Windows.Forms.Timer { Interval = 33 };
            refreshTimer.Tick += (s, args) =>
            {
                if (tabs.SelectedIndex == 0) vramCanvas.Invalidate();
                else if (tabs.SelectedIndex == 1) bgCanvas.Invalidate();
                else if (tabs.SelectedIndex == 2) oamCanvas.Invalidate(); // Refresh OAM tab
            };
            debugForm.FormClosed += (s, args) => refreshTimer.Stop();

            refreshTimer.Start();
            debugForm.Show();
        }
        private void ViewVram_Click(object sender, EventArgs e)
        {
            // 1. Create a new separate window
            Form vramForm = new Form
            {
                Text = "VRAM Viewer - Live",
                ClientSize = new System.Drawing.Size(128 * 2, 256 * 2), // 2x Scale
                FormBorderStyle = FormBorderStyle.FixedToolWindow,
                StartPosition = FormStartPosition.CenterScreen
            };

            // 2. Create a SkiaSharp control for this specific window
            SkiaSharp.Views.Desktop.SKControl vramCanvas = new SkiaSharp.Views.Desktop.SKControl
            {
                Dock = DockStyle.Fill
            };
            // --- HOVER INFO LOGIC ---
            vramCanvas.MouseMove += (s, mouseArgs) =>
            {
                int scale = 2; // Make sure this matches the scale factor used in your Paint event!

                // Convert the mouse coordinates back down to the 128x256 internal resolution
                int pixelX = mouseArgs.X / scale;
                int pixelY = mouseArgs.Y / scale;

                // Ensure we are inside the bounds of the image
                if (pixelX >= 0 && pixelX < 128 && pixelY >= 0 && pixelY < 256)
                {
                    // Calculate which of the 16x32 tiles the mouse is over
                    int tileCol = pixelX / 8;
                    int tileRow = pixelY / 8;
                    int tileIndex = (tileRow * 16) + tileCol;

                    // Calculate the actual memory address in VRAM (starts at 0x8000)
                    int vramAddress = 0x8000 + (tileIndex * 16);

                    // Update the Window Title bar with the live data!
                    vramForm.Text = $"VRAM - Tile ID: 0x{tileIndex:X2} | Address: 0x{vramAddress:X4}";
                }
            };
            // 3. Attach the rendering logic
            vramCanvas.PaintSurface += (s, args) =>
            {
                // Make sure to use YOUR actual variable name here (e.g., cpu, gb, etc.)
                if (bus?.ppu == null)
                {
                    args.Surface.Canvas.Clear(SkiaSharp.SKColors.Black);
                    return;
                }

                uint[] vramPixels = bus.ppu.GetVramTexture();
                var info = new SkiaSharp.SKImageInfo(128, 256, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
                // Inside your new ViewBgMap_Click method:

                // ... the rest is identical to the VRAM viewer code!

                using (var bitmap = new SkiaSharp.SKBitmap())
                {
                    unsafe
                    {
                        fixed (uint* ptr = vramPixels)
                        {
                            bitmap.InstallPixels(info, (IntPtr)ptr, info.RowBytes, delegate { });

                            int scale = 2; // We are scaling the 128x256 image by 2x
                            int scaledWidth = 128 * scale;
                            int scaledHeight = 256 * scale;

                            var destRect = new SkiaSharp.SKRect(0, 0, scaledWidth, scaledHeight);

                            // 1. Draw the VRAM Bitmap
                            using (var paint = new SkiaSharp.SKPaint { FilterQuality = SkiaSharp.SKFilterQuality.None })
                            {
                                args.Surface.Canvas.DrawBitmap(bitmap, destRect, paint);
                            }

                            // 2. Draw the Grid Lines on top!
                            // Using a semi-transparent white so it doesn't completely block the pixels underneath
                            using (var gridPaint = new SkiaSharp.SKPaint
                            {
                                Color = new SkiaSharp.SKColor(255, 255, 255, 75), // (R, G, B, Alpha)
                                StrokeWidth = 1,
                                IsAntialias = false
                            })
                            {
                                int tileSize = 8 * scale; // An 8-pixel tile scaled up by 2x is 16 pixels wide on screen

                                // Draw Vertical Lines
                                for (int x = 0; x <= scaledWidth; x += tileSize)
                                {
                                    args.Surface.Canvas.DrawLine(x, 0, x, scaledHeight, gridPaint);
                                }

                                // Draw Horizontal Lines
                                for (int y = 0; y <= scaledHeight; y += tileSize)
                                {
                                    args.Surface.Canvas.DrawLine(0, y, scaledWidth, y, gridPaint);
                                }
                            }
                        }
                    }
                }
            };

            // 4. Create a timer to refresh this window at ~30 FPS
            // This allows you to watch tiles animate in real-time without slowing down the main game!
            System.Windows.Forms.Timer refreshTimer = new System.Windows.Forms.Timer { Interval = 33 };
            refreshTimer.Tick += (s, args) => vramCanvas.Invalidate();

            // 5. Clean up when the user closes the debug window
            vramForm.FormClosed += (s, args) => refreshTimer.Stop();

            // 6. Start the timer and show the window
            vramForm.Controls.Add(vramCanvas);
            refreshTimer.Start();
            vramForm.Show(); // .Show() makes it non-blocking so the main game keeps running!
        }
        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            // Kill the emulator loop before Windows destroys the window
            isRunning = false;
            /*if (cpu != null)
            {
                cpu.Logger.Stop();
            }*/
        }
        private void RunEmulatorEngine()
        {
            // The authentic Game Boy refresh rate is 59.73 Hz.
            double baseFrameTimeMs = 1000.0 / 59.73;
            System.Diagnostics.Stopwatch frameTimer = new System.Diagnostics.Stopwatch();
            frameTimer.Start();

            while (isRunning)
            {
                // 1. DETERMINE TARGET SPEED
                bool runAsGbc = activeCartridge.ColorMode == GbcMode.CgbSupported || activeCartridge.ColorMode == GbcMode.CgbExclusive;
                bool isDoubleSpeed = runAsGbc && (bus.KEY1 & 0x80) != 0;
                int targetCycles = isDoubleSpeed ? CYCLES_PER_FRAME_GBC : CYCLES_PER_FRAME_DMG;

                // 2. EXECUTE EXACTLY ONE FRAME
                ulong startingCycles = cpu.TotalClockCycles;
                ulong cyclesExecuted = 0;

                while (isRunning && cyclesExecuted < (ulong)targetCycles)
                {
                    cpu.Step();
                    cyclesExecuted = cpu.TotalClockCycles - startingCycles;
                }

                // 3. DYNAMIC AUDIO SYNC (With Panic Pre-Buffering)
                double currentFrameTarget = baseFrameTimeMs;
                int bufferedAudio = bus.apu.waveProvider.BufferedBytes;

                if (bufferedAudio < 8820)
                {
                    // CRITICAL STARVATION (Under 3 frames of audio)!
                    // Burst frames instantly to pre-fill the sound card's buffer!
                    currentFrameTarget = 0;
                }
                else if (bufferedAudio > 44100)
                {
                    // Buffer is over 250ms, slow the emulator down slightly
                    currentFrameTarget += 1.0;
                }
                else if (bufferedAudio < 26460)
                {
                    // Buffer is under 150ms, speed the emulator up slightly
                    currentFrameTarget -= 1.0;
                }

                // 4. THE HIGH-RESOLUTION SPEED LIMITER
                while (frameTimer.Elapsed.TotalMilliseconds < currentFrameTarget)
                {
                    System.Threading.Thread.SpinWait(10);
                }

                frameTimer.Restart();
            }
        }
        private void OnFrameReadyToDraw()
        {
            // The PPU runs on the Task thread. We must jump back to the UI thread to draw.
            if (skiaControl == null || bus == null) return;
            if (skiaControl.InvokeRequired)
            {
                skiaControl.BeginInvoke(new Action(() => skiaControl.Invalidate()));
            }
            else
            {
                skiaControl.Invalidate();
            }
        }
        private void GenerateLcdMask(SKRect rect, float scaleX, float scaleY)
        {
            // Clear out the old mask if the window was resized
            if (cachedLcdMask != null) cachedLcdMask.Dispose();

            // Create a new blank canvas exactly the size of the screen
            cachedLcdMask = new SKBitmap((int)rect.Width, (int)rect.Height);

            using (SKCanvas maskCanvas = new SKCanvas(cachedLcdMask))
            {
                // Fill it with pure white (White + Multiply Blend Mode = Invisible!)
                maskCanvas.Clear(SKColors.White);

                // 1. Draw the Grid
                using (SKPaint gridPaint = new SKPaint { Color = new SKColor(0, 0, 0, 75), StrokeWidth = 1 })
                {
                    for (int x = 0; x <= 160; x++)
                        maskCanvas.DrawLine(x * scaleX, 0, x * scaleX, rect.Height, gridPaint);

                    for (int y = 0; y <= 144; y++)
                        maskCanvas.DrawLine(0, y * scaleY, rect.Width, y * scaleY, gridPaint);
                }

                // 2. Draw the Vignette over the grid
                using (SKPaint vignettePaint = new SKPaint())
                {
                    vignettePaint.Shader = SKShader.CreateRadialGradient(
                        new SKPoint(rect.Width / 2, rect.Height / 2),
                        Math.Max(rect.Width, rect.Height) / 1.3f,
                        new SKColor[] { new SKColor(255, 255, 255, 0), new SKColor(0, 0, 0, 90) },
                        new float[] { 0.4f, 1.0f },
                        SKShaderTileMode.Clamp);

                    maskCanvas.DrawRect(rect, vignettePaint);
                }
            }

            // Remember the size so we don't rebuild it next frame!
            lastMaskRect = rect;
        }
        // --- SKIASHARP RENDERING ---
        private void SkiaControl_PaintSurface(object sender, SKPaintSurfaceEventArgs e)
        {
            SKCanvas canvas = e.Surface.Canvas;
            canvas.Clear(SKColors.Black);

            // Wait until the emulator is fully loaded and running
            if (bus == null || bus.ppu == null || !isRunning) return;

            // 1. LOCK THE BUFFER SO THE PPU CANNOT OVERWRITE IT WHILE WE DRAW!
            lock (bus.ppu.BufferLock)
            {
                // 2. READ FROM THE NEW DisplayBuffer, NOT FrameBuffer!
                GCHandle handle = GCHandle.Alloc(bus.ppu.DisplayBuffer, GCHandleType.Pinned);

                try
                {
                    IntPtr pointer = handle.AddrOfPinnedObject();
                    SKImageInfo info = new SKImageInfo(160, 144, SKColorType.Bgra8888, SKAlphaType.Opaque);

                    using (SKBitmap bitmap = new SKBitmap())
                    {
                        bitmap.InstallPixels(info, pointer, info.RowBytes, delegate { }, null);
                        using (SKPaint paint = new SKPaint { FilterQuality = SKFilterQuality.None, IsAntialias = false })
                        {
                            if (useLcdFilter)
                            {
                                // 1. Apply the color bleed and blur directly to the game image
                                float[] gbcColorMatrix = new float[]
                                {
                                    0.75f, 0.15f, 0.05f, 0, 0,
                                    0.15f, 0.65f, 0.15f, 0, 0,
                                    0.05f, 0.15f, 0.65f, 0, 0,
                                    0,     0,     0,     1, 0
                                };
                                paint.ColorFilter = SKColorFilter.CreateColorMatrix(gbcColorMatrix);
                                paint.ImageFilter = SKImageFilter.CreateBlur(0.4f, 0.4f);
                            }

                            SKRect destinationRect = e.Info.Rect;

                            // Draw the game!
                            canvas.DrawBitmap(bitmap, destinationRect, paint);

                            if (useLcdFilter)
                            {
                                float scaleX = destinationRect.Width / 160f;
                                float scaleY = destinationRect.Height / 144f;

                                if (scaleX >= 2 && scaleY >= 2)
                                {
                                    // 2. CHECK CACHE: If the window size changed, rebuild the mask
                                    if (cachedLcdMask == null || lastMaskRect != destinationRect)
                                    {
                                        GenerateLcdMask(destinationRect, scaleX, scaleY);
                                    }

                                    // 3. DRAW CACHE: Stamp the pre-rendered mask over the game instantly
                                    using (SKPaint maskPaint = new SKPaint { BlendMode = SKBlendMode.Multiply })
                                    {
                                        canvas.DrawBitmap(cachedLcdMask, destinationRect, maskPaint);
                                    }
                                }
                            }
                        }
                    
                    }
                }
                finally
                {
                    handle.Free();
                }
            }
        }

        private void StartWebcam()
        {
            // 1. Find available webcams
            videoDevices = new FilterInfoCollection(FilterCategory.VideoInputDevice);

            if (videoDevices.Count > 0)
            {
                // 2. Select the default camera (Index 0)
                videoSource = new VideoCaptureDevice(videoDevices[0].MonikerString);

                // 3. Subscribe to the frame capture event
                videoSource.NewFrame += new NewFrameEventHandler(VideoSource_NewFrame);

                // 4. Start the camera
                videoSource.Start();
            }
            else
            {
                MessageBox.Show("No webcam detected!");
            }
        }

        // This fires automatically every time the webcam takes a picture (e.g., 30 FPS)
        private void VideoSource_NewFrame(object sender, NewFrameEventArgs eventArgs)
        {
            if (gbCameraMapper != null)
            {
                // Push the raw frame into our Game Boy Camera mapper!
                gbCameraMapper.UpdateWebcamFrame(eventArgs.Frame);
            }
        }

        // CRITICAL: You must stop the webcam when closing the emulator, 
        // otherwise it stays on in the background and locks the camera device!
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (videoSource != null && videoSource.IsRunning)
            {
                videoSource.SignalToStop();
                videoSource.WaitForStop();
            }
            base.OnFormClosing(e);
        }


    }
}