
using GameboyTest.Form_Design;
using GameboyTest.MBC;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using System;
using System.Drawing;
using System.Windows.Forms;

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
            //InitializeSkiaControl();

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

            ToolStripMenuItem viewMemoryItem = new ToolStripMenuItem("View Memory Banks");
            viewMemoryItem.Click += ViewMemory_Click;

            // --- NEW: Diagnostic Suite Item ---

            // Add items to Debug dropdown
            debugMenu.DropDownItems.Add(viewMemoryItem); // Added to the dropdown here

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
                        bus = new MemoryBus(activeMbc);

                        // 3. Create the CPU and connect it to the bus!
                        cpu = new CPU(bus);
                        isRunning = false;

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

        // --- SKIASHARP RENDERING ---

        private void GenerateTestPicture()
        {
            frameBuffer = new SKBitmap(160, 144);
            using (SKCanvas canvas = new SKCanvas(frameBuffer))
            {
                canvas.Clear(new SKColor(155, 188, 15));
                using (SKPaint paint = new SKPaint { Color = new SKColor(15, 56, 15), Style = SKPaintStyle.Fill })
                {
                    canvas.DrawRect(40, 40, 80, 64, paint);
                }
            }
        }

        private void SkiaControl_PaintSurface(object sender, SKPaintSurfaceEventArgs e)
        {
            SKCanvas canvas = e.Surface.Canvas;
            canvas.Clear(SKColors.Black);

            // Adjust destination rect to account for the menu bar pushing the canvas down
            SKRect destinationRect = e.Info.Rect;
            using (SKPaint paint = new SKPaint { FilterQuality = SKFilterQuality.None })
            {
                canvas.DrawBitmap(frameBuffer, destinationRect, paint);
            }
        }
        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            // Kill the emulator loop before Windows destroys the window
            isRunning = false;
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
    }
}