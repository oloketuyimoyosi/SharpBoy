using System;
using System.Drawing;
using System.Windows.Forms;
using SkiaSharp;
using SkiaSharp.Views.Desktop;

namespace GameboyTest
{
    public partial class Form1 : Form
    {
        private SKControl skiaControl;
        private SKBitmap frameBuffer;

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

        private void InitializeMenu()
        {
            MenuStrip menuStrip = new MenuStrip();

            // --- FILE MENU ---
            ToolStripMenuItem fileMenu = new ToolStripMenuItem("File");

            ToolStripMenuItem loadRomItem = new ToolStripMenuItem("Load ROM...");
            loadRomItem.Click += LoadRom_Click; // Hook up the click event

            ToolStripMenuItem exitItem = new ToolStripMenuItem("Exit");
            exitItem.Click += (s, ev) => this.Close();

            // Add items to the File dropdown
            fileMenu.DropDownItems.Add(loadRomItem);
            fileMenu.DropDownItems.Add(new ToolStripSeparator()); // Adds a nice dividing line
            fileMenu.DropDownItems.Add(exitItem);

            // --- DEBUG MENU ---
            ToolStripMenuItem debugMenu = new ToolStripMenuItem("Debug");

            ToolStripMenuItem viewVramItem = new ToolStripMenuItem("View VRAM");
            ToolStripMenuItem viewCpuItem = new ToolStripMenuItem("CPU State");

            // Add items to Debug dropdown
            debugMenu.DropDownItems.Add(viewVramItem);
            debugMenu.DropDownItems.Add(viewCpuItem);

            // Add both main menus to the bar
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

        private void LoadRom_Click(object sender, EventArgs e)
        {
            // Open a standard Windows file browser
            using (OpenFileDialog openFileDialog = new OpenFileDialog())
            {
                // Only allow the user to see .gb and .gbc files
                openFileDialog.Filter = "Game Boy ROMs (*.gb;*.gbc)|*.gb;*.gbc|All files (*.*)|*.*";
                openFileDialog.Title = "Select a Game Boy ROM";

                // If the user clicks "OK" in the file browser
                if (openFileDialog.ShowDialog() == DialogResult.OK)
                {
                    Cartridge gameCartridge = new Cartridge();

                    // Pass the selected file path to our Cartridge class
                    if (gameCartridge.LoadRom(openFileDialog.FileName))
                    {
                        // Update the window title to show the loaded game
                        this.Text = $"Game Boy Emulator - Running: {gameCartridge.Title}";
                        Console.WriteLine(gameCartridge.RomData);
                        // NOTE: Later, this is where you will tell your CPU to reset 
                        // and start executing the newly loaded ROM.
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
    }
}