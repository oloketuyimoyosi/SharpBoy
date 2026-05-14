using GameboyTest.Form_Design;
using GameboyTest.MBC;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using System.Runtime.InteropServices;

namespace GameboyTest
{
    public partial class Form1 : Form
    {
        private SKControl skiaControl;
        private SKBitmap frameBuffer;
        MemoryBus bus;
        CPU cpu;
        private bool isRunning = false;
        private System.Threading.Tasks.Task emulatorTask;

        public Form1()
        {
            InitializeComponent();
            this.Text = "Game Boy Emulator";
            this.ClientSize = new Size(480, 432);
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

                            bus = new MemoryBus(activeMbc, OnFrameReadyToDraw);
                            cpu = new CPU(bus);

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

            CheckBox chkUseMap2 = new CheckBox { Text = "Use Map 2 (0x9C00)", Location = new System.Drawing.Point(10, 20), Width = 150 };

            Label lblTileData = new Label { Text = "ID: --\nAddr: --", Dock = DockStyle.Top, Height = 40 };

            // --- 2. THE TABS & CANVASES ---
            TabControl tabs = new TabControl { Dock = DockStyle.Fill };

            // Helper function to get the current zoom scale multiplier
            int GetScale() => cmbZoom.SelectedIndex == 0 ? 1 : (cmbZoom.SelectedIndex == 1 ? 2 : 4);

            // VRAM TAB
            TabPage tabVram = new TabPage("VRAM");
            var vramCanvas = new SkiaSharp.Views.Desktop.SKControl { Dock = DockStyle.Fill };
            vramCanvas.PaintSurface += (s, args) =>
            {
                if (bus?.ppu == null) { args.Surface.Canvas.Clear(SkiaSharp.SKColors.Black); return; }

                // FUNCTIONAL PALETTE: Pass the dropdown index to the PPU!
                uint[] pixels = bus.ppu.GetVramTexture(cmbPalette.SelectedIndex);
                var info = new SkiaSharp.SKImageInfo(128, 256, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
                using (var bitmap = new SkiaSharp.SKBitmap())
                {
                    unsafe
                    {
                        fixed (uint* ptr = pixels)
                        {
                            bitmap.InstallPixels(info, (IntPtr)ptr, info.RowBytes, delegate { });
                            int scale = GetScale(); // FUNCTIONAL ZOOM!
                            var destRect = new SkiaSharp.SKRect(0, 0, 128 * scale, 256 * scale);

                            using (var paint = new SkiaSharp.SKPaint { FilterQuality = SkiaSharp.SKFilterQuality.None })
                                args.Surface.Canvas.DrawBitmap(bitmap, destRect, paint);

                            if (chkShowGrid.Checked) // FUNCTIONAL GRID!
                            {
                                using (var gridPaint = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(255, 255, 255, 75), StrokeWidth = 1 })
                                {
                                    int tileSize = 8 * scale;
                                    for (int x = 0; x <= 128 * scale; x += tileSize) args.Surface.Canvas.DrawLine(x, 0, x, 256 * scale, gridPaint);
                                    for (int y = 0; y <= 256 * scale; y += tileSize) args.Surface.Canvas.DrawLine(0, y, 128 * scale, y, gridPaint);
                                }
                            }
                        }
                    }
                }
            };

            // FUNCTIONAL HOVER INSPECTOR
            vramCanvas.MouseMove += (s, mouseArgs) =>
            {
                int scale = GetScale();
                int pixelX = mouseArgs.X / scale;
                int pixelY = mouseArgs.Y / scale;

                if (pixelX >= 0 && pixelX < 128 && pixelY >= 0 && pixelY < 256)
                {
                    int tileIndex = ((pixelY / 8) * 16) + (pixelX / 8);
                    lblTileData.Text = $"ID: 0x{tileIndex:X2}\nAddr: 0x{(0x8000 + (tileIndex * 16)):X4}";
                }
            };
            tabVram.Controls.Add(vramCanvas);

            // BG MAP TAB
            // BG MAP TAB
            TabPage tabBg = new TabPage("BG Map");
            var bgCanvas = new SkiaSharp.Views.Desktop.SKControl { Dock = DockStyle.Fill };
            bgCanvas.PaintSurface += (s, args) =>
            {
                if (bus?.ppu == null) { args.Surface.Canvas.Clear(SkiaSharp.SKColors.Black); return; }

                uint[] pixels = bus.ppu.GetBackgroundMapTexture(chkUseMap2.Checked);
                var info = new SkiaSharp.SKImageInfo(256, 256, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
                using (var bitmap = new SkiaSharp.SKBitmap())
                {
                    unsafe
                    {
                        fixed (uint* ptr = pixels)
                        {
                            bitmap.InstallPixels(info, (IntPtr)ptr, info.RowBytes, delegate { });
                            int scale = GetScale();

                            // 1. Draw the actual Map
                            using (var paint = new SkiaSharp.SKPaint { FilterQuality = SkiaSharp.SKFilterQuality.None })
                                args.Surface.Canvas.DrawBitmap(bitmap, new SkiaSharp.SKRect(0, 0, 256 * scale, 256 * scale), paint);

                            // 2. Draw the optional Grid
                            if (chkShowGrid.Checked)
                            {
                                using (var gridPaint = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(255, 255, 255, 75), StrokeWidth = 1 })
                                {
                                    int tileSize = 8 * scale;
                                    for (int x = 0; x <= 256 * scale; x += tileSize) args.Surface.Canvas.DrawLine(x, 0, x, 256 * scale, gridPaint);
                                    for (int y = 0; y <= 256 * scale; y += tileSize) args.Surface.Canvas.DrawLine(0, y, 256 * scale, y, gridPaint);
                                }
                            }

                            // 3. --- DRAW THE VIEWPORT CAMERA BOX ---
                            int scx = bus.ppu.SCX;
                            int scy = bus.ppu.SCY;

                            using (var viewPaint = new SkiaSharp.SKPaint
                            {
                                Color = SkiaSharp.SKColors.Red, // Bright red so it stands out!
                                Style = SkiaSharp.SKPaintStyle.Stroke,
                                StrokeWidth = 2 * scale // Thicker line based on zoom
                            })
                            {
                                // The Game Boy screen is 160x144.
                                // Because the map is 256x256 and wraps around, if the camera crosses the right 
                                // or bottom edge, it appears on the opposite side. 
                                // We draw the rectangle up to 4 times (offset by 256) to handle this wrapping perfectly!
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

            tabs.Controls.Add(tabVram);
            tabs.Controls.Add(tabBg);

            // --- 3. WIRE UP UI EVENTS (Instant Redraws) ---
            // When an option is changed, we force the canvases to redraw immediately!
            cmbZoom.SelectedIndexChanged += (s, ev) => { vramCanvas.Invalidate(); bgCanvas.Invalidate(); };
            cmbPalette.SelectedIndexChanged += (s, ev) => vramCanvas.Invalidate();
            chkShowGrid.CheckedChanged += (s, ev) => { vramCanvas.Invalidate(); bgCanvas.Invalidate(); };
            chkUseMap2.CheckedChanged += (s, ev) => bgCanvas.Invalidate();

            // --- 4. ASSEMBLE THE RIGHT PANEL ---
            Panel optionsPanel = new Panel { Dock = DockStyle.Right, Width = 200, Padding = new Padding(10), BackColor = System.Drawing.Color.WhiteSmoke };

            GroupBox grpMaps = new GroupBox { Text = "Background Maps", Dock = DockStyle.Top, Height = 50 };
            grpMaps.Controls.Add(chkUseMap2);

            GroupBox grpPalette = new GroupBox { Text = "Render Palette", Dock = DockStyle.Top, Height = 55 };
            grpPalette.Controls.Add(cmbPalette);

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
            // The Game Boy processes exactly 70,224 T-Cycles per 60Hz frame.
            // Eventually, we will track cycles here to sync the video and audio.

            while (isRunning)
            {
                // 1. Execute the next instruction
                cpu.Step();

                // 2. (Future) If 70,224 cycles have passed:
                //    - Tell SkiaSharp to draw the screen
                //    - Thread.Sleep() to lock the speed to 60 FPS
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
        // --- SKIASHARP RENDERING ---
        private void SkiaControl_PaintSurface(object sender, SKPaintSurfaceEventArgs e)
        {
            SKCanvas canvas = e.Surface.Canvas;
            canvas.Clear(SKColors.Black);

            // Wait until the emulator is fully loaded and running
            if (bus == null || bus.ppu == null || !isRunning) return;

            // 1. PIN THE PPU'S ARRAY IN RAM
            // This prevents the Garbage Collector from moving the memory while the GPU reads it
            GCHandle handle = GCHandle.Alloc(bus.ppu.FrameBuffer, GCHandleType.Pinned);

            try
            {
                IntPtr pointer = handle.AddrOfPinnedObject();

                // 2. TELL SKIA WHAT THE DATA LOOKS LIKE (160x144, 32-bit colors)
                SKImageInfo info = new SKImageInfo(160, 144, SKColorType.Bgra8888, SKAlphaType.Opaque);

                // 3. WRAP THE RAW POINTER IN A BITMAP
                using (SKBitmap bitmap = new SKBitmap())
                {
                    bitmap.InstallPixels(info, pointer, info.RowBytes, delegate { }, null);

                    // 4. DRAW IT!
                    // FilterQuality.None ensures the pixels stay sharp and blocky when scaled
                    using (SKPaint paint = new SKPaint { FilterQuality = SKFilterQuality.None, IsAntialias = false })
                    {
                        // e.Info.Rect is the exact size of your control (480x432).
                        // Drawing into this rect automatically handles your 3x scaling!
                        SKRect destinationRect = e.Info.Rect;
                        canvas.DrawBitmap(bitmap, destinationRect, paint);
                    }
                }
            }
            finally
            {
                // ALWAYS free the handle so C# can manage memory safely again
                handle.Free();
            }
        }

    }
}