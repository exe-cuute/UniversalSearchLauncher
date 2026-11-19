using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Universal Search Launcher Uninstall")]
[assembly: AssemblyProduct("Universal Search Launcher")]
[assembly: AssemblyCompany("Universal Search Launcher")]
[assembly: AssemblyVersion("1.0.1.0")]
[assembly: AssemblyFileVersion("1.0.1.0")]

namespace UniversalSearchLauncher.Uninstall
{
    internal static class Program
    {
        private const string ProductName = "Universal Search Launcher";
        private const string RunValueName = "Universal Search Launcher";
        private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\UniversalSearchLauncher";

        [STAThread]
        private static void Main(string[] args)
        {
            bool silent = Array.Exists(args, delegate(string value)
            {
                return value.Equals("/S", StringComparison.OrdinalIgnoreCase) ||
                       value.Equals("/silent", StringComparison.OrdinalIgnoreCase) ||
                       value.Equals("--silent", StringComparison.OrdinalIgnoreCase);
            });

            string installDirectory = Path.GetDirectoryName(Application.ExecutablePath);
            if (string.IsNullOrWhiteSpace(installDirectory))
            {
                return;
            }

            bool deleteData = false;
            if (!silent)
            {
                DialogResult confirm = MessageBox.Show(
                    "Удалить Universal Search Launcher с этого компьютера?\n\n" +
                    "Приложение будет закрыто, ярлыки и запись автозапуска будут удалены.",
                    "Удаление Universal Search Launcher",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2);
                if (confirm != DialogResult.Yes)
                {
                    return;
                }

                DialogResult data = MessageBox.Show(
                    "Удалить также настройки, историю поиска и индекс файлов?\n\n" +
                    "Выберите «Нет», чтобы сохранить их для будущей установки.",
                    "Universal Search Launcher",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2);
                deleteData = data == DialogResult.Yes;
            }

            try
            {
                CloseApplication();
                DeleteShortcuts();
                RemoveAutostart();
                RemoveUninstallEntry();

                if (deleteData)
                {
                    string dataDirectory = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "UniversalSearchLauncher");
                    TryDeleteDirectory(dataDirectory);
                }

                ScheduleDirectoryRemoval(installDirectory);
            }
            catch (Exception ex)
            {
                if (!silent)
                {
                    MessageBox.Show(
                        "Не удалось полностью удалить приложение.\n\n" + ex.Message,
                        "Universal Search Launcher",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
            }
        }

        private static void CloseApplication()
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

        private static void DeleteShortcuts()
        {
            TryDeleteFile(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                ProductName + ".lnk"));

            string startMenu = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                ProductName);
            TryDeleteDirectory(startMenu);
        }

        private static void RemoveAutostart()
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

        private static void RemoveUninstallEntry()
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, false);
            }
            catch
            {
            }
        }

        private static void ScheduleDirectoryRemoval(string directory)
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

        private static void TryDeleteFile(string path)
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

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch
            {
            }
        }
    }
}
