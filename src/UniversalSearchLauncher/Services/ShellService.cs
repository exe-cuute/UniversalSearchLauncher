using System.Diagnostics;
using UniversalSearchLauncher.Core.Models;

namespace UniversalSearchLauncher.Services;

public static class ShellService
{
    public static bool TryExecute(SearchResult result, out string error)
    {
        error = string.Empty;
        try
        {
            switch (result.Action)
            {
                case SearchActionType.LaunchApplication:
                    LaunchApplication(result);
                    return true;

                case SearchActionType.OpenFile:
                    OpenPath(result.Target, "Не удалось открыть файл.");
                    return true;

                case SearchActionType.OpenFolder:
                    OpenFolder(result.Target);
                    return true;

                case SearchActionType.RevealInExplorer:
                    Reveal(result.Target);
                    return true;

                case SearchActionType.OpenUrl:
                    OpenUrl(result.Target);
                    return true;

                case SearchActionType.SearchWeb:
                    OpenUrl(result.Target);
                    return true;

                default:
                    error = "Неизвестное действие.";
                    return false;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            AppLog.Error("Не удалось выполнить действие для " + result.Title, ex);
            return false;
        }
    }

    public static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException("Пустой URL.");
        }

        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    public static void OpenPath(string? path, string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path)))
        {
            throw new FileNotFoundException(errorMessage, path);
        }

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public static void OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            throw new DirectoryNotFoundException("Папка не найдена: " + path);
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    public static void Reveal(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path)))
        {
            throw new FileNotFoundException("Элемент не найден: " + path);
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    public static void OpenContainingFolder(SearchResult result)
    {
        if (result.Action == SearchActionType.OpenFolder && Directory.Exists(result.Target))
        {
            OpenFolder(result.Target);
            return;
        }

        if (result.Target is not null)
        {
            if (Directory.Exists(result.Target))
            {
                OpenFolder(result.Target);
            }
            else
            {
                Reveal(result.Target);
            }
        }
    }

    private static void LaunchApplication(SearchResult result)
    {
        switch (result.LaunchMode)
        {
            case LaunchMode.Aumid:
                if (string.IsNullOrWhiteSpace(result.Aumid))
                {
                    throw new InvalidOperationException("Не указан идентификатор приложения.");
                }

                Process.Start(new ProcessStartInfo(
                    "explorer.exe",
                    "shell:AppsFolder\\" + result.Aumid)
                {
                    UseShellExecute = true
                });
                break;

            case LaunchMode.Shortcut:
                if (string.IsNullOrWhiteSpace(result.Target) || !File.Exists(result.Target))
                {
                    throw new FileNotFoundException("Ярлык приложения не найден.", result.Target);
                }

                Process.Start(new ProcessStartInfo(result.Target) { UseShellExecute = true });
                break;

            default:
                if (string.IsNullOrWhiteSpace(result.Target) || !File.Exists(result.Target))
                {
                    throw new FileNotFoundException("Исполняемый файл приложения не найден.", result.Target);
                }

                var startInfo = new ProcessStartInfo(result.Target)
                {
                    UseShellExecute = true,
                    Arguments = result.Arguments ?? string.Empty
                };

                if (!string.IsNullOrWhiteSpace(result.WorkingDirectory) &&
                    Directory.Exists(result.WorkingDirectory))
                {
                    startInfo.WorkingDirectory = result.WorkingDirectory;
                }

                Process.Start(startInfo);
                break;
        }
    }
}
