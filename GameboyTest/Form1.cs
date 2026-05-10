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