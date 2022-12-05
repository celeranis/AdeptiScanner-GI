﻿using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Input;
using Tesseract;
using WindowsInput;

namespace AdeptiScanner_GI
{
    public partial class ScannerForm : Form
    {
        public static ScannerForm INSTANCE;
        private TesseractEngine tessEngine;
        private Bitmap img_Raw;
        private Bitmap img_Filtered;
        private int[] filtered_rows;
        private Rectangle savedArtifactArea = new Rectangle(0, 0, 1, 1);
        private Rectangle savedGameArea = new Rectangle(0,0,1,1);
        private bool pauseAuto = true;
        private bool softCancelAuto = true;
        private bool hardCancelAuto = true;
        private KeyHandler ghk;

        internal bool autoRunning = false;
        private bool autoCaptureDone = false;
        internal List<InventoryItem> scannedItems = new List<InventoryItem>();
        private bool cancelOCRThreads = false;
        private const int ThreadCount = 6; //--------------------------------------------------------
        private bool[] threadRunning = new bool[ThreadCount];
        private ConcurrentQueue<Bitmap>[] threadQueues = new ConcurrentQueue<Bitmap>[ThreadCount];
        private ConcurrentQueue<Bitmap> badResults = new ConcurrentQueue<Bitmap>();
        private TesseractEngine[] threadEngines = new TesseractEngine[ThreadCount];
        private List<InventoryItem>[] threadResults = new List<InventoryItem>[ThreadCount];
        private bool rememberSettings = true;

        internal int minLevel = 0;
        internal int maxLevel = 20;
        internal int minRarity = 5;
        internal int maxRarity = 5;
        internal bool exportAllEquipped = true;
        internal bool useTemplate = false;
        internal string travelerName = "";
        internal bool captureOnread = true;
        internal bool saveImagesGlobal = false;
        internal string clickSleepWait_load = "100";
        internal string scrollSleepWait_load = "1500";
        internal string scrollTestWait_load = "100";
        internal string recheckWait_load = "300";
        internal bool? updateData = null;
        internal bool? updateVersion = null;
        internal string ignoredDataVersion = "";
        internal string ignoredProgramVersion = "";
        internal string lastUpdateCheck = "";

        enum panelChoices
        {
            ExportFilters,
            ArtifactDetails
        };

        private static InputSimulator sim = new InputSimulator();

        public ScannerForm()
        {
            ScannerForm.INSTANCE = this;
            if (Directory.Exists(Database.appdataPath) && Database.appDir != Database.appdataPath)
            {
                foreach( string filePath in Directory.EnumerateFiles(Database.appdataPath))
                {
                    string fileName = filePath.Replace(Database.appdataPath, "");
                    string localFilePath = Database.appDir + fileName;
                    if (File.Exists(localFilePath))
                        File.Delete(localFilePath);
                    File.Copy(filePath, localFilePath);
                    File.Delete(filePath);
                }
                Directory.Delete(Database.appdataPath);
            }
            loadSettings();
            InitializeComponent();
            finalizeLoadSettings();
            label_dataversion.Text = "Data Version: " + Database.dataVersion;
            label_appversion.Text = "Program Version: " + Database.programVersion;
            this.Text = "AdeptiScanner_GI V" + Database.programVersion;
            FormClosing += eventFormClosing;
            Activated += eventGotFocus;
            try
            {
                tessEngine = new TesseractEngine(Database.appDir + @"/tessdata", "genshin")
                {
                    DefaultPageSegMode = PageSegMode.SingleLine
                };
            }
            catch (Exception e)
            {
                MessageBox.Show("Error trying to access Tessdata file" + Environment.NewLine + Environment.NewLine + 
                    "Exact error:" + Environment.NewLine + e.ToString(),
                    "Scanner could not start", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Environment.Exit(-1);
            }

            //worker thread stuff
            for (int i = 0; i < ThreadCount; i++)
            {
                threadRunning[i] = false;
                threadQueues[i] = new ConcurrentQueue<Bitmap>();
                threadEngines[i] = new TesseractEngine(Database.appDir + @"/tessdata", "genshin")
                {
                    DefaultPageSegMode = PageSegMode.SingleLine
                };
                threadResults[i] = new List<InventoryItem>();
            }

            //simple junk defaults
            img_Raw = new Bitmap(image_preview.Width, image_preview.Height);
            using (Graphics g = Graphics.FromImage(img_Raw))
            {
                g.FillRectangle(Brushes.Black, 0, 0, img_Raw.Width, img_Raw.Height);
                g.FillRectangle(Brushes.White, img_Raw.Width / 8, img_Raw.Height / 8, img_Raw.Width * 6 / 8, img_Raw.Height * 6 / 8);
            }
            img_Filtered = new Bitmap(img_Raw);
            image_preview.Image = new Bitmap(img_Raw);
            FormClosing += GenshinArtifactOCR_FormClosing;
            if (!updateData.HasValue || !updateVersion.HasValue)
            {
                Application.Run(new FirstStart());
            } else
            {
                searchForUpdates(false);
            }
        }

        private void eventFormClosing(object sender, FormClosingEventArgs e)
        {
            if (rememberSettings)
                saveSettings();
        }

        private void GenshinArtifactOCR_FormClosing(object sender, FormClosingEventArgs e)
        {
            unregisterKey();
        }

        private bool TryPauseAuto()
        {
            if (!pauseAuto && autoRunning)
            {
                pauseAuto = true;
                text_full.AppendText("Auto scanning paused, select action" + Environment.NewLine);
                button_hardCancel.Enabled = true;
                button_softCancel.Enabled = true;
                button_resume.Enabled = true;
                return true;
            }
            return false;
        }

        private void eventGotFocus(object sender, EventArgs e)
        {
            TryPauseAuto();
        }

        public void registerKey()
        {
            if (InvokeRequired)
            {
                this.Invoke(new Action(registerKey));
                return;
            }
            if (ghk == null)
            {
                ghk = new KeyHandler(Keys.Escape, this);
            }
            ghk.Register();
        }

        public void unregisterKey()
        {
            if (InvokeRequired)
            {
                this.Invoke(new Action(unregisterKey));
                return;
            }
            if (ghk == null)
            {
                return;
            }
            ghk.Unregister();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == KeyHandler.WM_HOTKEY_MSG_ID)
                TryPauseAuto();
            base.WndProc(ref m);
        }

        public void AppendStatusText(string value, bool setButtons)
        {
            if (InvokeRequired)
            {
                this.Invoke(new Action<string, bool>(AppendStatusText), new object[] { value, setButtons });
                return;
            }
            text_full.AppendText(value);
            if (setButtons)
            {
                btn_capture.Enabled = true;
                btn_OCR.Enabled = true;
                button_auto.Enabled = true;
                button_hardCancel.Enabled = false;
                button_softCancel.Enabled = false;
                button_resume.Enabled = false;
            }
        }


        /// <summary>
        /// Move cursor to position and simulare left click
        /// </summary>
        /// <param name="x">Cursor X position</param>
        /// <param name="y">Cursor Y position</param>
        private void clickPos(int x, int y)
        {
            System.Windows.Forms.Cursor.Position = new Point(x, y);
            sim.Mouse.LeftButtonClick();
        }

        /// <summary>
        /// Empties all text boxes
        /// </summary>
        private void resetTextBoxes()
        {
            text_full.Text = "";
            artifactDetails1.ResetText();
        }

        private void displayInventoryItem(InventoryItem item)
        {
            text_full.Text = item.ToString();
            artifactDetails1.DisplayArtifact(item);
        }

        //https://stackoverflow.com/questions/11660184/c-sharp-check-if-run-as-administrator
        public static bool IsAdministrator()
        {
            return (new WindowsPrincipal(WindowsIdentity.GetCurrent()))
                      .IsInRole(WindowsBuiltInRole.Administrator);
        }

        private void runOCRThread(int threadIndex)
        {
            Task.Run((() =>
            {
                threadRunning[threadIndex] = true;
                bool saveImages = false;
                while (autoRunning && !cancelOCRThreads)
                {
                    if (threadQueues[threadIndex].TryDequeue(out Bitmap img))
                    {
                        //string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH-mm-ssff");
                        //if (saveImages)
                        //    img.Save(Database.appDir + @"\images\GenshinArtifactImg " + timestamp + ".png");
                        Rectangle area = new Rectangle(0, 0, img.Width, img.Height);
                        Bitmap filtered = new Bitmap(img);
                        filtered = ImageProcessing.getArtifactImg(filtered, area, out int[] rows, saveImages, out bool locked, out int rarity, out Rectangle typeMainArea, out Rectangle levelArea, out Rectangle subArea, out Rectangle setArea, out Rectangle charArea);

                        InventoryItem item = ImageProcessing.getArtifacts(filtered, rows, saveImages, threadEngines[threadIndex], locked, rarity, typeMainArea, levelArea, subArea, setArea, charArea);

                        if (Database.artifactInvalid(rarity, item))
                        {
                            badResults.Enqueue(img);
                        }
                        else
                        {
                            threadResults[threadIndex].Add(item);
                        }
                    }
                    else if (autoCaptureDone || softCancelAuto || hardCancelAuto)
                    {
                        threadRunning[threadIndex] = false;
                        return;
                    }
                    else
                    {
                        System.Threading.Thread.Sleep(1000);
                    }
                }
                threadRunning[threadIndex] = false;

            }));
        }

        private void runAuto(bool saveImages, int clickSleepWait = 100, int scrollSleepWait = 1500, int scrollTestWait = 100, int recheckSleepWait = 300)
        {
            text_full.Text = "Starting auto-run. ---Press ESCAPE to pause---" + Environment.NewLine + "If no artifact switching happens, you forgot to run as admin" + Environment.NewLine;
            autoRunning = true;
            autoCaptureDone = false;
            registerKey(); //activate pause auto hotkey
            //start worker threads
            for (int i = 0; i < ThreadCount; i++)
            {
                threadQueues[i] = new ConcurrentQueue<Bitmap>();
                threadResults[i] = new List<InventoryItem>();
                runOCRThread(i);
            }

            Task.Run((() =>
            {
                Stopwatch runtime = new Stopwatch();
                runtime.Start();
                System.Security.Cryptography.SHA1CryptoServiceProvider sha1 = new System.Security.Cryptography.SHA1CryptoServiceProvider();
                bool running = true;
                bool firstRun = true;
                int firstY = 0;
                int firstRows = 0;
                int nextThread = 0;
                Rectangle gridArea = new Rectangle(savedGameArea.X, savedGameArea.Y, savedArtifactArea.X - savedGameArea.X, savedGameArea.Height);
                Point gridOffset = new Point(gridArea.X, gridArea.Y);
                List<string> foundArtifactHashes = new List<string>();

                //make sure cursor is on the correct screen
                System.Threading.Thread.Sleep(50);
                System.Windows.Forms.Cursor.Position = new Point(0, 0);
                System.Threading.Thread.Sleep(50);
                System.Windows.Forms.Cursor.Position = new Point(0, 0);
                System.Threading.Thread.Sleep(50);

                while (running)
                {
                    //load current grid/scroll location
                    Bitmap img = ImageProcessing.CaptureScreenshot(saveImages, gridArea, true);
                    List<Point> artifactLocations = ImageProcessing.getArtifactGrid(img, saveImages, gridOffset);

                    if (artifactLocations.Count == 0)
                    {
                        break;
                    }
                    int startTop = artifactLocations[0].Y;
                    int startBot = startTop;
                    int rows = 1;
                    int distToScroll = 0;
                    foreach (Point p in artifactLocations)
                    {
                        if (p.Y > startBot + 3)
                        {
                            startBot = p.Y;
                            rows++;
                        }
                    }
                    if (firstRun)
                    {
                        firstY = startTop;
                        firstRows = rows;
                    }

                    if (rows >= 1)
                    {
                        distToScroll = (int)((startBot - (double)firstY) / rows * (rows + 1));
                    }

                    if (!firstRun)
                    {
                        while (pauseAuto)
                        {
                            if (hardCancelAuto)
                            {
                                goto hard_cancel_pos;
                            }
                            if (softCancelAuto)
                            {
                                running = false;
                                pauseAuto = false;
                                goto soft_cancel_pos;
                            }
                            System.Threading.Thread.Sleep(1000);
                        }
                        //test scroll distance
                        sim.Mouse.VerticalScroll(-1);
                        System.Threading.Thread.Sleep(scrollTestWait);
                        sim.Mouse.VerticalScroll(-1);
                        System.Threading.Thread.Sleep(scrollTestWait);
                        img = ImageProcessing.CaptureScreenshot(saveImages, gridArea, true);
                        artifactLocations = ImageProcessing.getArtifactGrid(img, saveImages, gridOffset);

                        if (artifactLocations.Count == 0)
                        {
                            break;
                        }
                        int distPerScroll = (startTop - artifactLocations[0].Y ) / 2;
                        int scrollsNeeded = 0;
                        if (distPerScroll > 0)
                        {
                            scrollsNeeded = distToScroll / distPerScroll;
                        }

                        if (scrollsNeeded <= 0 || distPerScroll == 0 || rows < Math.Max(firstRows - 1, 0))
                        {
                            running = false;
                        }
                        /*Console.WriteLine("firstY " + firstY + Environment.NewLine
                            + "startTop " + startTop + Environment.NewLine
                            + "currTop " + artifactLocations[0].Y + Environment.NewLine
                            + "distPerScroll " + distPerScroll + Environment.NewLine
                            + "distToScroll " + distToScroll + Environment.NewLine
                            + "scrollsNeeded " + scrollsNeeded + Environment.NewLine + Environment.NewLine); */

                        while (scrollsNeeded > 0)
                        {
                            sim.Mouse.VerticalScroll(-1);
                            scrollsNeeded--;
                        }
                        System.Threading.Thread.Sleep(scrollSleepWait);
                        img = ImageProcessing.CaptureScreenshot(saveImages, gridArea, true);
                        artifactLocations = ImageProcessing.getArtifactGrid(img, saveImages, gridOffset);
                    }


                    firstRun = false;

                    //select and OCR each artifact in list
                    bool repeat = false;
                    for (int i = 0; i < artifactLocations.Count;)
                    {
                        Point p = artifactLocations[i];
                        while (pauseAuto)
                        {
                            if (hardCancelAuto)
                            {
                                goto hard_cancel_pos;
                            }

                            if (softCancelAuto)
                            {
                                running = false;
                                pauseAuto = false;
                                goto soft_cancel_pos;
                            }
                            System.Threading.Thread.Sleep(1000);
                        }
                        clickPos(p.X, p.Y);
                        System.Threading.Thread.Sleep(clickSleepWait);

                        Bitmap artifactSC = ImageProcessing.CaptureScreenshot(saveImages, savedArtifactArea, true);

                        //check if artifact already found using hash of pixels, without the right edge due to lock/unlock animation
                        Bitmap withoutLock = new Bitmap(artifactSC.Width * 3 / 4, artifactSC.Height);
                        using (Graphics g = Graphics.FromImage(withoutLock))
                        {
                            g.DrawImage(artifactSC, 0, 0, new Rectangle(0, 0, artifactSC.Width * 3 / 4, artifactSC.Height), GraphicsUnit.Pixel);
                        }
                        BitmapData imgData = withoutLock.LockBits(new Rectangle(0, 0, withoutLock.Width, withoutLock.Height), ImageLockMode.ReadWrite, withoutLock.PixelFormat);
                        int numBytes = Math.Abs(imgData.Stride) * imgData.Height;
                        byte[] imgBytes = new byte[numBytes];
                        Marshal.Copy(imgData.Scan0, imgBytes, 0, numBytes);
                        int PixelSize = 4; //ARGB, reverse order
                        withoutLock.UnlockBits(imgData);
                        //https://stackoverflow.com/a/800469 with some liberty
                        string hash = string.Concat(sha1.ComputeHash(imgBytes).Select(x => x.ToString("X2")));

                        if (foundArtifactHashes.Contains(hash))
                        {
                            if (repeat)
                            {
                                if (running)
                                {
                                    AppendStatusText("Duplicate artifact found, stopping after this screen", false);
                                }
                                running = false;
                                Console.WriteLine("Duplicate at " + p.ToString());
                                repeat = false;
                                i++;
                                continue;
                            }
                            else
                            {
                                repeat = true;
                                Console.WriteLine("Rechecking at " + p.ToString());
                                System.Threading.Thread.Sleep(recheckSleepWait);
                                continue;
                            }

                        }
                        foundArtifactHashes.Add(hash);

                        //queue up processing of artifact
                        threadQueues[nextThread].Enqueue(artifactSC);
                        nextThread = (nextThread + 1) % ThreadCount;

                        i++;
                        repeat = false;
                    }

                }

            soft_cancel_pos:

                autoCaptureDone = true;


                AppendStatusText("Scanning complete, awaiting results" + Environment.NewLine
                    + "Time elapsed: " + runtime.ElapsedMilliseconds + "ms" + Environment.NewLine, false);
                for (int i = 0; i < ThreadCount; i++)
                {
                    while (threadRunning[i] || pauseAuto)
                    {
                        if (hardCancelAuto)
                        {
                            goto hard_cancel_pos;
                        }
                        System.Threading.Thread.Sleep(1000);
                    }
                    foreach (InventoryItem item in threadResults[i])
                    {
                        scannedItems.Add(item);
                    }
                }


                AppendStatusText("Auto finished" + Environment.NewLine
                    + " Good results: " + scannedItems.Count + ", Bad results: " + badResults.Count + Environment.NewLine
                    + "Time elapsed: " + runtime.ElapsedMilliseconds + "ms" + Environment.NewLine + Environment.NewLine, false);

                while (badResults.TryDequeue(out Bitmap img))
                {
                    Rectangle area = new Rectangle(0, 0, img.Width, img.Height);
                    Bitmap filtered = new Bitmap(img);
                    string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH-mm-ssff");
                    filtered.Save(Database.appDir + @"\images\GenshinArtifactImg " + timestamp + ".png");
                    filtered = ImageProcessing.getArtifactImg(filtered, area, out int[] rows, true, out bool locked, out int rarity, out Rectangle typeMainArea, out Rectangle levelArea, out Rectangle subArea, out Rectangle setArea, out Rectangle charArea);
                    InventoryItem item = ImageProcessing.getArtifacts(filtered, rows, true, tessEngine, locked, rarity, typeMainArea, levelArea, subArea, setArea, charArea);
                    AppendStatusText(item.ToString() + Environment.NewLine, false);
                }

                AppendStatusText("All bad results displayed" + Environment.NewLine, false);

            hard_cancel_pos:
                unregisterKey();
                runtime.Stop();
                AppendStatusText("Time elapsed: " + runtime.ElapsedMilliseconds + "ms" + Environment.NewLine, true);
                autoRunning = false;
            }));
        }

        enum CaptureDebugMode { 
            Off, 
            FullScreen,
            GameWindow,
            ArtifactArea
        };

        private void btn_capture_Click(object sender, EventArgs e)
        {
            bool saveImages = checkbox_saveImages.Checked;
            if (autoRunning)
            {
                text_full.AppendText("Ignored, auto currently running" + Environment.NewLine);
                return;
            }
            resetTextBoxes();

            //Nothing = Normal, LShift + LCtrl = Game, LShift = Artifact, LCtrl = FullScreen
            CaptureDebugMode debugMode = CaptureDebugMode.Off;
            if (Keyboard.IsKeyDown(Key.LeftShift))
            {
                if (Keyboard.IsKeyDown(Key.LeftCtrl))
                {
                    debugMode = CaptureDebugMode.GameWindow;
                } else
                {
                    debugMode = CaptureDebugMode.ArtifactArea;
                }
            } else if (Keyboard.IsKeyDown(Key.LeftCtrl)) 
            {
                debugMode = CaptureDebugMode.FullScreen;
            }

            if (debugMode != CaptureDebugMode.Off)
            {
                img_Raw = ImageProcessing.LoadScreenshot();
            } else
            {
                img_Raw = ImageProcessing.CaptureScreenshot(saveImages, Rectangle.Empty);
            }

            Rectangle? tmpGameArea = new Rectangle(0, 0, img_Raw.Width, img_Raw.Height);
            if (debugMode == CaptureDebugMode.Off || debugMode == CaptureDebugMode.FullScreen)
            {
                tmpGameArea = ImageProcessing.findGameArea(img_Raw);
                if (tmpGameArea == null)
                {
                    MessageBox.Show("Failed to find Game Area" + Environment.NewLine +
                        "Please make sure you're following the instructions properly."
                        + Environment.NewLine + "If the problem persists, please contact scanner dev", "Failed to find Game Area"
                        , MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }

            bool ArtifactAreaCaptured = tmpGameArea != null;
            if (debugMode == CaptureDebugMode.ArtifactArea)
            {
                savedArtifactArea = new Rectangle(0, 0, img_Raw.Width, img_Raw.Height);
            } else if (tmpGameArea != null)
            {
                try
                {
                    savedArtifactArea = ImageProcessing.findArtifactArea(img_Raw, tmpGameArea.Value);
                    if (savedArtifactArea.Width == 0 || savedArtifactArea.Height == 0)
                        throw new Exception("Detected artifact are has width or height 0");
                }
                catch (Exception exc)
                {
                    MessageBox.Show("Failed to find Artifact Area" + Environment.NewLine +
                        "Please make sure you're following the instructions properly."
                        + Environment.NewLine + "If the problem persists, please contact scanner dev"
                        + Environment.NewLine + Environment.NewLine + "---" + Environment.NewLine + Environment.NewLine + "Exact error message: " + Environment.NewLine + exc.ToString(), "Failed to find Artifact Area"
                        , MessageBoxButtons.OK, MessageBoxIcon.Error);
                    ArtifactAreaCaptured = false;
                }
            }

            if (ArtifactAreaCaptured)
            {
                savedGameArea = tmpGameArea.Value;
                btn_OCR.Enabled = true;
                button_auto.Enabled = true;
            }

            string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH-mm-ssff");
            if (saveImages)
            {
                if (tmpGameArea != null)
                {
                    Bitmap gameImg = new Bitmap(tmpGameArea.Value.Width, tmpGameArea.Value.Height);
                    using (Graphics g = Graphics.FromImage(gameImg))
                    {
                        g.DrawImage(img_Raw, 0, 0, tmpGameArea.Value, GraphicsUnit.Pixel);
                    }
                    gameImg.Save(Database.appDir + @"\images\GenshinGameArea " + timestamp + ".png");
                }

                if (ArtifactAreaCaptured)
                {
                    Bitmap artifactImg = new Bitmap(savedArtifactArea.Width, savedArtifactArea.Height);
                    using (Graphics g = Graphics.FromImage(artifactImg))
                    {
                        g.DrawImage(img_Raw, 0, 0, savedArtifactArea, GraphicsUnit.Pixel);
                    }
                    artifactImg.Save(Database.appDir + @"\images\GenshinArtifactArea " + timestamp + ".png");
                }
            }

            if (ArtifactAreaCaptured)
            {
                image_preview.Image = new Bitmap(savedArtifactArea.Width, savedArtifactArea.Height);
                using (Graphics g = Graphics.FromImage(image_preview.Image))
                {
                    g.DrawImage(img_Raw, 0, 0, savedArtifactArea, GraphicsUnit.Pixel);
                }
            }
        }

        private void btn_OCR_Click(object sender, EventArgs e)
        {
            bool saveImages = checkbox_saveImages.Checked;
            if (autoRunning)
            {
                text_full.AppendText("Ignored, auto currently running" + Environment.NewLine);
                return;
            }

            resetTextBoxes();

            if (checkbox_OCRcapture.Checked)
            {
                if (Keyboard.IsKeyDown(Key.LeftShift))
                {
                    img_Raw = ImageProcessing.LoadScreenshot();
                    savedGameArea = new Rectangle(0, 0, img_Raw.Width, img_Raw.Height);
                    savedArtifactArea = new Rectangle(0, 0, img_Raw.Width, img_Raw.Height);
                }
                else
                {
                    img_Raw = ImageProcessing.CaptureScreenshot(saveImages, Rectangle.Empty);
                }
            }


            img_Filtered = new Bitmap(img_Raw);
            InventoryItem artifact;

            img_Filtered = ImageProcessing.getArtifactImg(img_Filtered, savedArtifactArea, out filtered_rows, saveImages, out bool locked, out int rarity, out Rectangle typeMainArea, out Rectangle levelArea, out Rectangle subArea, out Rectangle setArea, out Rectangle charArea);
            artifact = ImageProcessing.getArtifacts(img_Filtered, filtered_rows, saveImages, tessEngine, locked, rarity, typeMainArea, levelArea, subArea, setArea, charArea);
            if (Database.artifactInvalid(rarity, artifact))
            {
                displayInventoryItem(artifact);
                text_full.AppendText(Environment.NewLine + "---This artifact is invalid---" + Environment.NewLine);
            } else
            {
                scannedItems.Add(artifact);
                displayInventoryItem(artifact);
            }
            text_full.AppendText(Environment.NewLine + "Total stored artifacts:" + scannedItems.Count + Environment.NewLine);

            image_preview.Image = new Bitmap(img_Filtered);
        }

        private void button_auto_Click(object sender, EventArgs e)
        {
            if (!IsAdministrator() && !Keyboard.IsKeyDown(Key.LeftShift))
            {
                MessageBox.Show("Cannot automatically scroll artifacts without admin perms" + Environment.NewLine + Environment.NewLine 
                + "To use auto mode, restart scanner as admin",
                "Insufficient permissions", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            bool saveImages = checkbox_saveImages.Checked;
            if (autoRunning)
            {
                text_full.AppendText("Ignored, auto currently running" + Environment.NewLine);
                return;
            }
            btn_OCR.Enabled = false;
            btn_capture.Enabled = false;
            button_auto.Enabled = false;
            pauseAuto = false;
            softCancelAuto = false;
            hardCancelAuto = false;

            int.TryParse(text_clickSleepWait.Text, out int clickSleepWait);
            if (clickSleepWait == 0)
                clickSleepWait = 100;
            int.TryParse(text_ScrollSleepWait.Text, out int scrollSleepWait);
            if (scrollSleepWait == 0)
                scrollSleepWait = 1500;
            int.TryParse(text_ScrollTestWait.Text, out int scrollTestWait);
            if (scrollTestWait == 0)
                scrollTestWait = 100;
            int.TryParse(text_RecheckWait.Text, out int recheckWait);
            if (recheckWait == 0)
                recheckWait = 300;
            runAuto(false, clickSleepWait, scrollSleepWait, scrollTestWait, recheckWait);
        }

        private void button_resume_Click(object sender, EventArgs e)
        {
            text_full.AppendText("Resuming auto" + Environment.NewLine);
            pauseAuto = false;
        }

        private void button_softCancel_Click(object sender, EventArgs e)
        {
            text_full.AppendText("New scanning canceled, awaiting results" + Environment.NewLine);
            softCancelAuto = true;
        }

        private void button_hardCancel_Click(object sender, EventArgs e)
        {
            text_full.AppendText("Auto canceled" + Environment.NewLine);
            hardCancelAuto = true;
        }

        private void button_export_Click(object sender, EventArgs e)
        {
            if (autoRunning)
            {
                text_full.AppendText("Ignored, auto currently running" + Environment.NewLine);
                return;
            }
            string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH-mm-ssff");
            JObject currData = InventoryItem.listToGOODArtifacts(scannedItems, minLevel, maxLevel, minRarity, maxRarity, exportAllEquipped);
            if (useTemplate && !File.Exists(Database.appDir + @"\ExportTemplate.json"))
            {
                MessageBox.Show("No export template found, exporting without one" + Environment.NewLine + "To use an export template, place valid GOOD-format json in ScannerFiles and rename to \"ExportTemplate.json\"", 
                    "No export template found", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                useTemplate = false;
            }
            if (useTemplate)
            {
                JObject template = JsonConvert.DeserializeObject<JObject>(File.ReadAllText(Database.appDir + @"\ExportTemplate.json"));
                template.Remove("artifacts");
                template.Add("artifacts", currData["artifacts"]);
                currData = template;
            }
            else
            {
                currData.Add("format", "GOOD");
                currData.Add("version", 1);
                currData.Add("source", "AdeptiScanner");
                //currData.Add("characters", new JArray());
                //currData.Add("weapons", new JArray());
            }

            string fileName = Database.appDir + @"\export" + timestamp + ".GOOD.json";
            File.WriteAllText(fileName, currData.ToString());
            text_full.AppendText("Exported to \"" + fileName + "\"" + Environment.NewLine);

        }

        private void linkLabel1_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            System.Diagnostics.Process.Start("https://github.com/D1firehail/AdeptiScanner-GI");
        }

        private void loadSettings()
        {
            string fileName = Database.appDir + @"\settings.json";
            JObject settings = null;
            try
            {
                settings = JsonConvert.DeserializeObject<JObject>(File.ReadAllText(fileName));
            } catch (Exception e)
            {
                return;
            }

            if (settings.ContainsKey("TravelerName"))
            {
                string readTravelerName = settings["TravelerName"].ToObject<string>();
                Database.SetTravelerName(readTravelerName);
                travelerName = readTravelerName;
            }
            if (settings.ContainsKey("FilterMinLevel"))
            {
                minLevel = settings["FilterMinLevel"].ToObject<int>();
            }
            if (settings.ContainsKey("FilterMaxLevel"))
            {
                maxLevel = settings["FilterMaxLevel"].ToObject<int>();
            }
            if (settings.ContainsKey("FilterMinRarity"))
            {
                minRarity = settings["FilterMinRarity"].ToObject<int>();
            }
            if (settings.ContainsKey("FilterMaxRarity"))
            {
                maxRarity = settings["FilterMaxRarity"].ToObject<int>();
            }
            if (settings.ContainsKey("ExportUseTemplate"))
            {
                useTemplate = settings["ExportUseTemplate"].ToObject<bool>();
            }
            if (settings.ContainsKey("ExportAllEquipped"))
            {
                exportAllEquipped = settings["ExportAllEquipped"].ToObject<bool>();
            }
            if (settings.ContainsKey("CaptureOnRead"))
            {
                captureOnread = settings["CaptureOnRead"].ToObject<bool>();
            }
            if (settings.ContainsKey("saveImagesGlobal"))
            {
                saveImagesGlobal = settings["saveImagesGlobal"].ToObject<bool>();
            }
            if (settings.ContainsKey("clickSleepWait"))
            {
                clickSleepWait_load = settings["clickSleepWait"].ToObject<string>();
            }
            if (settings.ContainsKey("scrollSleepWait"))
            {
                scrollSleepWait_load = settings["scrollSleepWait"].ToObject<string>();
            }
            if (settings.ContainsKey("scrollTestWait"))
            {
                scrollTestWait_load = settings["scrollTestWait"].ToObject<string>();
            }
            if (settings.ContainsKey("recheckWait"))
            {
                recheckWait_load = settings["recheckWait"].ToObject<string>();
            }
            if (settings.ContainsKey("updateData"))
            {
                updateData = settings["updateData"].ToObject<bool>();
            }
            if (settings.ContainsKey("updateVersion"))
            {
                updateVersion = settings["updateVersion"].ToObject<bool>();
            }
            if (settings.ContainsKey("ignoredDataVersion"))
            {
                ignoredDataVersion = settings["ignoredDataVersion"].ToObject<string>();
            }
            if (settings.ContainsKey("ignoredProgramVersion"))
            {
                ignoredProgramVersion = settings["ignoredProgramVersion"].ToObject<string>();
            }
            if (settings.ContainsKey("lastUpdateCheck"))
            {
                lastUpdateCheck = settings["lastUpdateCheck"].ToObject<string>();
            }
        }

        private void finalizeLoadSettings()
        {
            text_traveler.Text = travelerName;
            checkbox_OCRcapture.Checked = captureOnread;
            checkbox_saveImages.Checked = saveImagesGlobal;
            text_clickSleepWait.Text = clickSleepWait_load;
            text_ScrollSleepWait.Text = scrollSleepWait_load;
            text_ScrollTestWait.Text = scrollTestWait_load;
            text_RecheckWait.Text = recheckWait_load;

            if (updateData.HasValue)
                checkBox_updateData.Checked = updateData.Value;
            if (updateVersion.HasValue)
                checkBox_updateVersion.Checked = updateVersion.Value;

        }

        public void SetUpdatePreferences(bool updateData, bool updateVersion)
        {
            if (InvokeRequired)
            {
                this.Invoke(new Action<bool, bool>(SetUpdatePreferences), new object[] { updateData, updateVersion });
                return;
            }
            this.updateData = updateData;
            this.updateVersion = updateVersion;
            checkBox_updateData.Checked = updateData;
            checkBox_updateVersion.Checked = updateVersion;
        }

        private void searchForUpdates(bool isManual)
        {
            string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd");
            if (timestamp == lastUpdateCheck && !isManual)
                return;
            lastUpdateCheck = timestamp;
            WebClient webclient = new WebClient();
            webclient.Headers.Add("user-agent", "AdeptiScanner");

            string programVersionTitle = "";
            string programVersionBody = "";

            if (isManual || (this.updateVersion.HasValue && this.updateVersion.Value))
            {
                try
                {
                    string response = webclient.DownloadString("https://api.github.com/repos/D1firehail/AdeptiScanner-GI/releases");
                    JArray releases = JsonConvert.DeserializeObject<JArray>(response);
                    if (releases.First.HasValues)
                    {
                        JObject latest = releases.First.Value<JObject>();
                        programVersionTitle = latest["tag_name"].ToObject<string>();

                        programVersionBody = latest["body"].ToObject<string>();

                    }
                } catch(Exception exc)
                {
                    Debug.WriteLine(exc.ToString());
                }
                if (programVersionTitle.ToLower().Equals("v"+Database.programVersion))
                {
                    programVersionTitle = "";
                }
            }

            string dataVersionString = "";
            string dataVersionJson = "";

            if (isManual || (this.updateData.HasValue && this.updateData.Value))
            {
                try
                {
                    string response = webclient.DownloadString("https://raw.githubusercontent.com/D1firehail/AdeptiScanner-GI/master/AdeptiScanner%20GI/ScannerFiles/ArtifactInfo.json");
                    JObject artifactInfo = JsonConvert.DeserializeObject<JObject>(response);
                    dataVersionString = artifactInfo["DataVersion"].ToObject<string>();
                    dataVersionJson = response;
                }
                catch (Exception exc)
                {
                    Debug.WriteLine(exc.ToString());
                }
                if (dataVersionString.Equals(Database.dataVersion))
                {
                    dataVersionString = "";
                }
            }
            if ( (programVersionTitle.Length > 0 && (isManual || programVersionTitle != ignoredProgramVersion)) 
                || (dataVersionString.Length > 0 && (isManual || dataVersionString != ignoredDataVersion)))
            {
                UpdatePrompt tmp = new UpdatePrompt(programVersionTitle, programVersionBody, dataVersionString, dataVersionJson);
                tmp.Show();
            } else if (isManual)
            {
                MessageBox.Show("No updates found",
                "Update checker", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

        }

        internal void executeDataUpdate(string newData)
        {
            if (InvokeRequired)
            {
                this.Invoke(new Action<string>(executeDataUpdate), new object[] { newData });
                return;
            }


            try
            {
                File.WriteAllText(Database.appDir + @"\ArtifactInfo.json", newData);
            } catch (Exception exc)
            {
                MessageBox.Show("Update failed, error: " + Environment.NewLine + Environment.NewLine + exc.ToString(), "Update failed",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            Application.Restart();
        }

        internal void readyVersionUpdate()
        {
            if (InvokeRequired)
            {
                this.Invoke(new Action(readyVersionUpdate));
                return;
            }

            //TODO export settings and template to %appdata% for retrieval in new version

            saveSettings();

            if (!Directory.Exists(Database.appdataPath))
                Directory.CreateDirectory(Database.appdataPath);

            string[] filesToCopy = { @"\settings.json", @"\ExportTemplate.json"};

            foreach (string file in filesToCopy)
            {
                if (!File.Exists(Database.appDir + file))
                    continue;
                if (File.Exists(Database.appdataPath + file))
                    File.Delete(Database.appdataPath + file);
                File.Copy(Database.appDir + file, Database.appdataPath + file);
            }
            System.Diagnostics.Process.Start("https://github.com/D1firehail/AdeptiScanner-GI/releases/latest");
            Application.Exit();
        }

        internal void setIgnoredVersions(string dataVersion, string programVersion)
        {
            if (InvokeRequired)
            {
                this.Invoke(new Action<string, string>(setIgnoredVersions), new object[] { dataVersion, programVersion });
                return;
            }
            if (dataVersion.Length > 0)
                ignoredDataVersion = dataVersion;
            if (programVersion.Length > 0)
                ignoredProgramVersion = programVersion;
        }

        private void saveSettings()
        {
            JObject settings = new JObject();
            settings["TravelerName"] = text_traveler.Text;
            settings["FilterMinLevel"] = minLevel;
            settings["FilterMaxLevel"] = maxLevel;
            settings["FilterMinRarity"] = minRarity;
            settings["FilterMaxRarity"] = maxRarity;
            settings["ExportUseTemplate"] = useTemplate;
            settings["ExportAllEquipped"] = exportAllEquipped;
            settings["CaptureOnRead"] = captureOnread;
            settings["saveImagesGlobal"] = saveImagesGlobal;
            settings["clickSleepWait"] = text_clickSleepWait.Text;
            settings["scrollSleepWait"] = text_ScrollSleepWait.Text;
            settings["scrollTestWait"] = text_ScrollTestWait.Text;
            settings["recheckWait"] = text_RecheckWait.Text;
            if (updateData.HasValue)
                settings["updateData"] = updateData.Value;
            if (updateVersion.HasValue)
                settings["updateVersion"] = updateVersion.Value;
            settings["ignoredDataVersion"] = ignoredDataVersion;
            settings["ignoredProgramVersion"] = ignoredProgramVersion;
            settings["lastUpdateCheck"] = lastUpdateCheck;
            

            string fileName = Database.appDir + @"\settings.json";
            File.WriteAllText(fileName, settings.ToString());
        }


        private void text_traveler_TextChanged(object sender, EventArgs e)
        {
            travelerName = text_traveler.Text;
            Database.SetTravelerName(travelerName);
        }

        private void checkbox_OCRcapture_CheckedChanged(object sender, EventArgs e)
        {
            captureOnread = checkbox_OCRcapture.Checked;
        }

        private void checkbox_saveImages_CheckedChanged(object sender, EventArgs e)
        {
            saveImagesGlobal = checkbox_saveImages.Checked;
        }

        private void button_loadArtifacts_Click(object sender, EventArgs e)
        {
            if (autoRunning)
            {
                text_full.AppendText("Ignored, auto currently running" + Environment.NewLine);
                return;
            }
            using (OpenFileDialog openFileDialog = new OpenFileDialog())
            {
                openFileDialog.InitialDirectory = Database.appDir;
                openFileDialog.Filter = "All files (*.*)|*.*";
                openFileDialog.FilterIndex = 1;
                openFileDialog.RestoreDirectory = true;
                openFileDialog.Multiselect = false;

                if (openFileDialog.ShowDialog() == DialogResult.OK)
                {
                    foreach (string file in openFileDialog.FileNames)
                    {
                        try
                        {
                            int startArtiAmount = scannedItems.Count();
                            JObject GOODjson = JsonConvert.DeserializeObject<JObject>(File.ReadAllText(file));
                            if (GOODjson.ContainsKey("artifacts"))
                            {
                                JArray artifacts = GOODjson["artifacts"].ToObject<JArray>();
                                foreach (JObject artifact in artifacts)
                                {
                                    InventoryItem importedArtifact = InventoryItem.fromGOODArtifact(artifact);
                                    if (importedArtifact != null)
                                    {
                                        scannedItems.Add(importedArtifact);
                                    }
                                }
                            }
                            int endArtiAmount = scannedItems.Count();
                            text_full.AppendText("Imported " + (endArtiAmount - startArtiAmount) + " aritfacts (new total " + endArtiAmount + ") from file: " + file + Environment.NewLine);
                            break;
                        }
                        catch (Exception exc)
                        {
                            text_full.AppendText("Error importing from file: " + file + Environment.NewLine);
                            Debug.WriteLine(exc);
                        }
                    }
                }
            }
        }

        private void button_resetSettings_Click(object sender, EventArgs e)
        {
            DialogResult dialogResult = MessageBox.Show("This will make all settings return to default the next time the scanner is started" + Environment.NewLine + "Are you sure?", "Remove saved Settings", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (dialogResult == DialogResult.Yes)
            {
                File.Delete(Database.appDir + @"\settings.json");
                rememberSettings = false;
            }
        }

        private void checkBox_updateData_CheckedChanged(object sender, EventArgs e)
        {
            updateData = checkBox_updateData.Checked;
        }

        private void checkBox_updateVersion_CheckedChanged(object sender, EventArgs e)
        {
            updateVersion = checkBox_updateVersion.Checked;
        }

        private void button_checkUpdateManual_Click(object sender, EventArgs e)
        {
            searchForUpdates(true);
        }
    }
}
