using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Universal Search Launcher Setup")]
[assembly: AssemblyProduct("Universal Search Launcher")]
[assembly: AssemblyCompany("Universal Search Launcher")]
[assembly: AssemblyDescription("Установщик Universal Search Launcher")]
[assembly: AssemblyVersion("1.0.1.0")]
[assembly: AssemblyFileVersion("1.0.1.0")]

namespace UniversalSearchLauncher.Setup
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool silent = args.Any(IsSilentSwitch);
            string target = Installer.DefaultInstallDirectory;
            foreach (string arg in args)
            {
                if (arg.StartsWith("/DIR=", StringComparison.OrdinalIgnoreCase))
                {
                    target = arg.Substring(5).Trim('"');
                }
                else if (arg.StartsWith("/D=", StringComparison.OrdinalIgnoreCase))
                {
                    target = arg.Substring(3).Trim('"');
                }
            }

            if (silent)
            {
                try
                {
                    Installer.Install(target, true, true, false, null);
                    Environment.ExitCode = 0;
                }
                catch (Exception ex)
                {
                    Installer.WriteErrorLog(ex);
                    Environment.ExitCode = 1;
                }

                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                Application.Run(new SetupForm(target));
            }
            catch (Exception ex)
            {
                Installer.WriteErrorLog(ex);
                MessageBox.Show(
                    "Не удалось запустить установщик.\n\n" + ex.Message,
                    "Universal Search Launcher",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private static bool IsSilentSwitch(string value)
        {
            return value.Equals("/S", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("/silent", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("--silent", StringComparison.OrdinalIgnoreCase);
        }
    }

    internal sealed class ProgressInfo
    {
        public ProgressInfo(int percent, string stage)
        {
            Percent = percent;
            Stage = stage;
        }

        public int Percent { get; private set; }
        public string Stage { get; private set; }
    }

    internal static class Installer
    {
        public const string ProductName = "Universal Search Launcher";
        public const string ApplicationFile = "UniversalSearchLauncher.exe";
        public const string UninstallerFile = "Uninstall.exe";
        public const string Version = "1.0.1";
        public const string RunValueName = "Universal Search Launcher";
        public const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\UniversalSearchLauncher";
        private const string MarkerFile = ".usl-install";
        private const string PayloadResource = "Payload.zip";

        public static string DefaultInstallDirectory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs",
                    ProductName);
            }
        }

        public static void Install(
            string targetDirectory,
            bool desktopShortcut,
            bool autostart,
            bool launch,
            IProgress<ProgressInfo> progress)
        {
            if (string.IsNullOrWhiteSpace(targetDirectory))
            {
                throw new InvalidOperationException("Не задана папка установки.");
            }

            string target = Path.GetFullPath(targetDirectory.Trim().Trim('"'));
            string root = Path.GetPathRoot(target);
            if (string.IsNullOrEmpty(root) || target.TrimEnd('\\').Equals(root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Нельзя устанавливать приложение в корень диска.");
            }

            if (Directory.Exists(target) &&
                Directory.EnumerateFileSystemEntries(target).Any() &&
                !File.Exists(Path.Combine(target, MarkerFile)))
            {
                // The form asks for confirmation before getting here. This guard is for silent installs.
                if (progress == null)
                {
                    throw new InvalidOperationException("Папка установки не пуста: " + target);
                }
            }

            Report(progress, 2, "Закрываем запущенное приложение…");
            CloseRunningApplication();

            Directory.CreateDirectory(target);
            Report(progress, 5, "Распаковываем файлы…");
            ExtractPayload(target, progress);

            string executable = Path.Combine(target, ApplicationFile);
            if (!File.Exists(executable))
            {
                throw new FileNotFoundException("В установочном пакете отсутствует исполняемый файл.", executable);
            }

            Report(progress, 84, "Создаём ярлыки…");
            Directory.CreateDirectory(Path.GetDirectoryName(executable));
            string startMenuFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                ProductName);
            Directory.CreateDirectory(startMenuFolder);
            CreateShortcut(
                Path.Combine(startMenuFolder, ProductName + ".lnk"),
                executable,
                target,
                ProductName,
                executable);
            string uninstaller = Path.Combine(target, UninstallerFile);
            if (File.Exists(uninstaller))
            {
                CreateShortcut(
                    Path.Combine(startMenuFolder, "Удалить " + ProductName + ".lnk"),
                    uninstaller,
                    target,
                    "Удалить " + ProductName,
                    uninstaller);
            }

            string desktopLink = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                ProductName + ".lnk");
            if (desktopShortcut)
            {
                CreateShortcut(desktopLink, executable, target, ProductName, executable);
            }
            else
            {
                TryDeleteFile(desktopLink);
            }

            Report(progress, 90, "Настраиваем автозапуск и удаление…");
            using (RegistryKey runKey = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run",
                RegistryKeyPermissionCheck.ReadWriteSubTree))
            {
                if (autostart)
                {
                    runKey.SetValue(
                        RunValueName,
                        "\"" + executable + "\" --background",
                        RegistryValueKind.String);
                }
                else
                {
                    runKey.DeleteValue(RunValueName, false);
                }
            }

            long footprint = 0;
            try
            {
                footprint = new DirectoryInfo(target)
                    .EnumerateFiles("*", SearchOption.AllDirectories)
                    .Sum(file => file.Length);
            }
            catch
            {
                footprint = 0;
            }

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(
                UninstallKeyPath,
                RegistryKeyPermissionCheck.ReadWriteSubTree))
            {
                key.SetValue("DisplayName", ProductName, RegistryValueKind.String);
                key.SetValue("DisplayVersion", Version, RegistryValueKind.String);
                key.SetValue("Publisher", ProductName, RegistryValueKind.String);
                key.SetValue("DisplayIcon", executable + ",0", RegistryValueKind.String);
                key.SetValue("InstallLocation", target, RegistryValueKind.String);
                key.SetValue("UninstallString", "\"" + uninstaller + "\"", RegistryValueKind.String);
                key.SetValue("QuietUninstallString", "\"" + uninstaller + "\" /S", RegistryValueKind.String);
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("EstimatedSize", (int)Math.Max(1, footprint / 1024), RegistryValueKind.DWord);
            }

            try
            {
                File.WriteAllText(Path.Combine(target, MarkerFile), Version);
            }
            catch
            {
                // Non-critical metadata.
            }

            Report(progress, 100, "Готово.");
            if (launch)
            {
                Process.Start(new ProcessStartInfo(executable)
                {
                    UseShellExecute = true,
                    WorkingDirectory = target
                });
            }
        }

        public static bool IsApplicationRunning()
        {
            return Process.GetProcessesByName("UniversalSearchLauncher").Length > 0;
        }

        public static void CloseRunningApplication()
        {
            foreach (Process process in Process.GetProcessesByName("UniversalSearchLauncher"))
            {
                try
                {
                    if (process.Id == Process.GetCurrentProcess().Id)
                    {
                        continue;
                    }

                    process.CloseMainWindow();
                    if (!process.WaitForExit(1200))
                    {
                        process.Kill();
                        process.WaitForExit(1500);
                    }
                }
                catch
                {
                    try
                    {
                        process.Kill();
                    }
                    catch
                    {
                    }
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        public static string GetStartMenuFolder()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), ProductName);
        }

        public static string GetDesktopShortcut()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ProductName + ".lnk");
        }

        public static void CreateShortcut(
            string shortcutPath,
            string targetPath,
            string workingDirectory,
            string description,
            string iconPath)
        {
            object shell = null;
            object shortcut = null;
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null)
                {
                    throw new InvalidOperationException("Компонент WScript.Shell недоступен.");
                }

                shell = Activator.CreateInstance(shellType);
                dynamic shellObject = shell;
                shortcut = shellObject.CreateShortcut(shortcutPath);
                dynamic shortcutObject = shortcut;
                shortcutObject.TargetPath = targetPath;
                shortcutObject.WorkingDirectory = workingDirectory;
                shortcutObject.Description = description;
                shortcutObject.IconLocation = iconPath + ",0";
                shortcutObject.Save();
            }
            finally
            {
                if (shortcut != null && Marshal.IsComObject(shortcut))
                {
                    Marshal.FinalReleaseComObject(shortcut);
                }

                if (shell != null && Marshal.IsComObject(shell))
                {
                    Marshal.FinalReleaseComObject(shell);
                }
            }
        }

        public static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        public static void DeleteShortcuts()
        {
            TryDeleteFile(GetDesktopShortcut());
            string startMenu = GetStartMenuFolder();
            try
            {
                if (Directory.Exists(startMenu))
                {
                    Directory.Delete(startMenu, true);
                }
            }
            catch
            {
            }
        }

        public static void RemoveAutostart()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run",
                    true))
                {
                    if (key != null)
                    {
                        key.DeleteValue(RunValueName, false);
                    }
                }
            }
            catch
            {
            }
        }

        public static void RemoveUninstallEntry()
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, false);
            }
            catch
            {
            }
        }

        public static void ScheduleDirectoryRemoval(string directory)
        {
            string temp = Path.GetTempPath();
            string command = "/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"" + directory + "\"";
            ProcessStartInfo info = new ProcessStartInfo("cmd.exe", command);
            info.CreateNoWindow = true;
            info.UseShellExecute = false;
            info.WindowStyle = ProcessWindowStyle.Hidden;
            info.WorkingDirectory = temp;
            Process.Start(info);
        }

        public static void WriteErrorLog(Exception ex)
        {
            string[] directories = new string[]
            {
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "UniversalSearchLauncher"),
                Path.Combine(Path.GetTempPath(), "UniversalSearchLauncher"),
                Path.GetDirectoryName(Application.ExecutablePath)
            };

            foreach (string directory in directories)
            {
                if (string.IsNullOrWhiteSpace(directory))
                {
                    continue;
                }

                try
                {
                    Directory.CreateDirectory(directory);
                    File.AppendAllText(
                        Path.Combine(directory, "installer.log"),
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine + ex + Environment.NewLine);
                    return;
                }
                catch
                {
                }
            }
        }

        private static void ExtractPayload(string target, IProgress<ProgressInfo> progress)
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResource))
            {
                if (stream == null)
                {
                    throw new InvalidOperationException("Установочный пакет повреждён: отсутствует ресурс Payload.zip.");
                }

                using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    int total = Math.Max(1, archive.Entries.Count);
                    int index = 0;
                    string prefix = target.TrimEnd('\\') + "\\";

                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        index++;
                        string relative = entry.FullName.Replace('/', '\\');
                        string destination = Path.GetFullPath(Path.Combine(target, relative));
                        if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                            !destination.Equals(target, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException("Небезопасный путь в установочном пакете: " + entry.FullName);
                        }

                        if (string.IsNullOrEmpty(entry.Name))
                        {
                            Directory.CreateDirectory(destination);
                            continue;
                        }

                        string parent = Path.GetDirectoryName(destination);
                        if (!string.IsNullOrEmpty(parent))
                        {
                            Directory.CreateDirectory(parent);
                        }

                        entry.ExtractToFile(destination, true);
                        if (index % 20 == 0 || index == total)
                        {
                            int percent = 5 + (int)(79.0 * index / total);
                            Report(progress, percent, "Распаковываем файлы… " + index + " / " + total);
                        }
                    }
                }
            }
        }

        private static void Report(IProgress<ProgressInfo> progress, int percent, string stage)
        {
            if (progress != null)
            {
                progress.Report(new ProgressInfo(percent, stage));
            }
        }
    }

    internal sealed class SwitchCheckBox : CheckBox
    {
        private static readonly Color Accent = Color.FromArgb(108, 99, 255);
        private static readonly Color TrackOff = Color.FromArgb(63, 69, 88);
        private static readonly Color Knob = Color.FromArgb(245, 247, 255);

        public SwitchCheckBox()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint |
                ControlStyles.ResizeRedraw,
                true);
            Cursor = Cursors.Hand;
            BackColor = Color.FromArgb(15, 17, 24);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(BackColor);

            int top = (Height - 16) / 2;
            Rectangle track = new Rectangle(1, top, 32, 16);
            using (GraphicsPath path = RoundRect(track, 8))
            using (SolidBrush brush = new SolidBrush(Checked ? Accent : TrackOff))
            {
                e.Graphics.FillPath(brush, path);
            }

            int knobX = Checked ? 17 : 3;
            using (SolidBrush knob = new SolidBrush(Knob))
            {
                e.Graphics.FillEllipse(knob, knobX, top + 2, 12, 12);
            }

            TextRenderer.DrawText(
                e.Graphics,
                Text,
                Font,
                new Rectangle(42, 0, Width - 44, Height),
                ForeColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        protected override void OnCheckedChanged(EventArgs e)
        {
            Invalidate();
            base.OnCheckedChanged(e);
        }

        private static GraphicsPath RoundRect(Rectangle rectangle, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int diameter = radius * 2;
            path.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 270, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class SetupForm : Form
    {
        private readonly TextBox _pathBox;
        private readonly CheckBox _desktopCheck;
        private readonly CheckBox _autostartCheck;
        private readonly CheckBox _launchCheck;
        private readonly Label _statusLabel;
        private readonly Panel _progressTrack;
        private readonly Panel _progressFill;
        private readonly Button _installButton;
        private readonly Button _cancelButton;
        private readonly Button _closeButton;
        private readonly Button _browseButton;
        private bool _installing;
        private bool _installed;

        public SetupForm(string initialTarget)
        {
            Text = "Установка Universal Search Launcher";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(680, 548);
            MinimumSize = new Size(680, 548);
            MaximumSize = new Size(680, 548);
            BackColor = Color.FromArgb(15, 17, 24);
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 9.5f);
            DoubleBuffered = true;
            KeyPreview = true;

            Icon appIcon = null;
            try
            {
                appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (appIcon != null)
                {
                    Icon = appIcon;
                }
            }
            catch
            {
            }

            Panel header = new Panel();
            header.Bounds = new Rectangle(0, 0, ClientSize.Width, 92);
            header.BackColor = Color.Transparent;
            header.MouseDown += HeaderMouseDown;
            Controls.Add(header);

            PictureBox logo = new PictureBox();
            logo.Bounds = new Rectangle(28, 24, 46, 46);
            logo.SizeMode = PictureBoxSizeMode.Zoom;
            logo.BackColor = Color.Transparent;
            logo.Image = appIcon == null ? null : appIcon.ToBitmap();
            header.Controls.Add(logo);

            Label title = new Label();
            title.Text = "Universal Search Launcher";
            title.Bounds = new Rectangle(88, 23, 480, 26);
            title.Font = new Font("Segoe UI Semibold", 16f);
            title.ForeColor = Color.FromArgb(245, 247, 255);
            title.BackColor = Color.Transparent;
            title.MouseDown += HeaderMouseDown;
            header.Controls.Add(title);

            Label subtitle = new Label();
            subtitle.Text = "Установка приложения для Windows 10 и Windows 11";
            subtitle.Bounds = new Rectangle(90, 50, 480, 22);
            subtitle.Font = new Font("Segoe UI", 9.5f);
            subtitle.ForeColor = Color.FromArgb(159, 168, 190);
            subtitle.BackColor = Color.Transparent;
            subtitle.MouseDown += HeaderMouseDown;
            header.Controls.Add(subtitle);

            _closeButton = CreateButton("×", Color.Transparent, Color.FromArgb(174, 181, 200));
            _closeButton.Bounds = new Rectangle(628, 24, 32, 30);
            _closeButton.Font = new Font("Segoe UI", 15f);
            _closeButton.FlatAppearance.BorderSize = 0;
            _closeButton.Click += delegate
            {
                if (!_installing)
                {
                    Close();
                }
            };
            header.Controls.Add(_closeButton);

            Label headline = new Label();
            headline.Text = "Поиск приложений, файлов и интернета — в одном окне";
            headline.Bounds = new Rectangle(40, 114, 600, 26);
            headline.Font = new Font("Segoe UI Semibold", 12.5f);
            headline.ForeColor = Color.FromArgb(236, 239, 250);
            headline.BackColor = Color.Transparent;
            Controls.Add(headline);

            Label description = new Label();
            description.Text =
                "Установщик разместит приложение в папке текущего пользователя, создаст ярлыки " +
                "и при необходимости добавит приложение в автозапуск. Права администратора не требуются.";
            description.Bounds = new Rectangle(40, 145, 600, 48);
            description.Font = new Font("Segoe UI", 9.5f);
            description.ForeColor = Color.FromArgb(160, 168, 187);
            description.BackColor = Color.Transparent;
            Controls.Add(description);

            Label pathLabel = new Label();
            pathLabel.Text = "Папка установки";
            pathLabel.Bounds = new Rectangle(40, 207, 200, 20);
            pathLabel.Font = new Font("Segoe UI Semibold", 9.5f);
            pathLabel.ForeColor = Color.FromArgb(217, 223, 238);
            pathLabel.BackColor = Color.Transparent;
            Controls.Add(pathLabel);

            _pathBox = new TextBox();
            _pathBox.Bounds = new Rectangle(40, 230, 526, 28);
            _pathBox.Text = initialTarget;
            _pathBox.BackColor = Color.FromArgb(34, 38, 51);
            _pathBox.ForeColor = Color.FromArgb(238, 241, 250);
            _pathBox.BorderStyle = BorderStyle.FixedSingle;
            _pathBox.Font = new Font("Segoe UI", 9.5f);
            Controls.Add(_pathBox);

            _browseButton = CreateButton("Обзор…", Color.FromArgb(41, 46, 61), Color.White);
            _browseButton.Bounds = new Rectangle(574, 229, 66, 30);
            _browseButton.Click += BrowseClick;
            Controls.Add(_browseButton);

            _desktopCheck = CreateCheckBox("Создать ярлык на рабочем столе", 40, 280, true);
            _autostartCheck = CreateCheckBox("Запускать Universal Search Launcher при входе в Windows", 40, 307, true);
            _launchCheck = CreateCheckBox("Запустить приложение после установки", 40, 334, true);

            _progressTrack = new Panel();
            _progressTrack.Bounds = new Rectangle(40, 393, 600, 5);
            _progressTrack.BackColor = Color.FromArgb(44, 49, 65);
            Controls.Add(_progressTrack);

            _progressFill = new Panel();
            _progressFill.Bounds = new Rectangle(0, 0, 0, 5);
            _progressFill.BackColor = Color.FromArgb(112, 101, 255);
            _progressTrack.Controls.Add(_progressFill);

            _statusLabel = new Label();
            _statusLabel.Text = "Готово к установке";
            _statusLabel.Bounds = new Rectangle(40, 406, 600, 24);
            _statusLabel.Font = new Font("Segoe UI", 9f);
            _statusLabel.ForeColor = Color.FromArgb(150, 158, 180);
            _statusLabel.BackColor = Color.Transparent;
            Controls.Add(_statusLabel);

            _cancelButton = CreateButton("Отмена", Color.FromArgb(36, 40, 53), Color.FromArgb(220, 225, 238));
            _cancelButton.Bounds = new Rectangle(430, 470, 100, 38);
            _cancelButton.Click += delegate
            {
                if (!_installing)
                {
                    Close();
                }
            };
            Controls.Add(_cancelButton);

            _installButton = CreateButton("Установить", Color.FromArgb(108, 99, 255), Color.White);
            _installButton.Bounds = new Rectangle(540, 470, 100, 38);
            _installButton.Font = new Font("Segoe UI Semibold", 9.5f);
            _installButton.Click += InstallClick;
            Controls.Add(_installButton);

            UpdateRoundedRegion();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Color.FromArgb(15, 17, 24));

            using (LinearGradientBrush glow = new LinearGradientBrush(
                new Rectangle(0, 0, Math.Max(1, ClientSize.Width), 230),
                Color.FromArgb(66, 108, 99, 255),
                Color.FromArgb(0, 108, 99, 255),
                90f))
            {
                e.Graphics.FillRectangle(glow, 0, 0, ClientSize.Width, 230);
            }

            using (Pen pen = new Pen(Color.FromArgb(45, 52, 72)))
            {
                e.Graphics.DrawLine(pen, 24, 91, ClientSize.Width - 24, 91);
            }

            base.OnPaint(e);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateRoundedRegion();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_installing)
            {
                e.Cancel = true;
                return;
            }

            base.OnFormClosing(e);
        }

        private void BrowseClick(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Выберите папку для установки Universal Search Launcher";
                dialog.ShowNewFolderButton = true;
                dialog.SelectedPath = _pathBox.Text;
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _pathBox.Text = dialog.SelectedPath;
                }
            }
        }

        private async void InstallClick(object sender, EventArgs e)
        {
            if (_installing)
            {
                return;
            }

            if (_installed)
            {
                Close();
                return;
            }

            string target = _pathBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(target))
            {
                MessageBox.Show(this, "Укажите папку установки.", "Universal Search Launcher",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                target = Path.GetFullPath(target);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Некорректный путь: " + ex.Message, "Universal Search Launcher",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (Directory.Exists(target) &&
                Directory.EnumerateFileSystemEntries(target).Any() &&
                !File.Exists(Path.Combine(target, ".usl-install")))
            {
                DialogResult overwrite = MessageBox.Show(
                    this,
                    "Выбранная папка не пуста. Установить приложение в неё, не удаляя существующие файлы?",
                    "Universal Search Launcher",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);
                if (overwrite != DialogResult.Yes)
                {
                    return;
                }
            }

            if (Installer.IsApplicationRunning())
            {
                DialogResult close = MessageBox.Show(
                    this,
                    "Universal Search Launcher сейчас запущен. Закрыть его для установки обновления?",
                    "Universal Search Launcher",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);
                if (close != DialogResult.Yes)
                {
                    return;
                }
            }

            _installing = true;
            SetControlsEnabled(false);
            _installButton.Text = "Установка…";
            _statusLabel.Text = "Подготовка…";
            _progressFill.Width = 0;

            Progress<ProgressInfo> progress = new Progress<ProgressInfo>(delegate(ProgressInfo info)
            {
                int width = (int)(_progressTrack.Width * (info.Percent / 100.0));
                _progressFill.Width = Math.Max(0, Math.Min(_progressTrack.Width, width));
                _statusLabel.Text = info.Stage;
            });

            try
            {
                bool desktop = _desktopCheck.Checked;
                bool autostart = _autostartCheck.Checked;
                bool launch = _launchCheck.Checked;
                await Task.Run(delegate
                {
                    Installer.Install(target, desktop, autostart, launch, progress);
                });

                _installed = true;
                _installing = false;
                _progressFill.Width = _progressTrack.Width;
                _statusLabel.Text = "Установлено. Universal Search Launcher готов к работе.";
                _statusLabel.ForeColor = Color.FromArgb(120, 227, 180);
                _installButton.Text = "Завершить";
                _installButton.Enabled = true;
                _cancelButton.Visible = false;
                _pathBox.Enabled = false;
                _browseButton.Enabled = false;
                _desktopCheck.Enabled = false;
                _autostartCheck.Enabled = false;
                _launchCheck.Enabled = false;
            }
            catch (Exception ex)
            {
                _installing = false;
                SetControlsEnabled(true);
                _installButton.Text = "Повторить";
                _installButton.Enabled = true;
                _statusLabel.Text = "Установка не завершена.";
                _statusLabel.ForeColor = Color.FromArgb(255, 128, 143);
                Installer.WriteErrorLog(ex);
                MessageBox.Show(
                    this,
                    "Не удалось установить приложение.\n\n" + ex.Message +
                    "\n\nПодробности записаны в installer.log.",
                    "Universal Search Launcher",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void SetControlsEnabled(bool enabled)
        {
            _pathBox.Enabled = enabled;
            _browseButton.Enabled = enabled;
            _desktopCheck.Enabled = enabled;
            _autostartCheck.Enabled = enabled;
            _launchCheck.Enabled = enabled;
            _cancelButton.Enabled = enabled;
            _installButton.Enabled = enabled;
        }

        private CheckBox CreateCheckBox(string text, int x, int y, bool isChecked)
        {
            SwitchCheckBox check = new SwitchCheckBox();
            check.Text = text;
            check.Bounds = new Rectangle(x, y, 590, 26);
            check.Checked = isChecked;
            check.ForeColor = Color.FromArgb(222, 227, 240);
            check.BackColor = Color.FromArgb(15, 17, 24);
            check.Font = new Font("Segoe UI", 9.5f);
            Controls.Add(check);
            return check;
        }

        private static Button CreateButton(string text, Color background, Color foreground)
        {
            Button button = new Button();
            button.Text = text;
            button.BackColor = background;
            button.ForeColor = foreground;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = Color.FromArgb(56, 62, 82);
            button.FlatAppearance.BorderSize = background == Color.Transparent ? 0 : 1;
            button.Cursor = Cursors.Hand;
            button.Font = new Font("Segoe UI", 9.5f);
            button.UseVisualStyleBackColor = false;
            return button;
        }

        private void HeaderMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                ReleaseCapture();
                SendMessage(Handle, 0xA1, new IntPtr(0x2), IntPtr.Zero);
            }
        }

        private void UpdateRoundedRegion()
        {
            try
            {
                Region old = Region;
                Region = Region.FromHrgn(CreateRoundRectRgn(0, 0, Width + 1, Height + 1, 18, 18));
                if (old != null)
                {
                    old.Dispose();
                }
            }
            catch
            {
            }
        }

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }
}
