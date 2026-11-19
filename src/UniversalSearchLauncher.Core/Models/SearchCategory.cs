namespace UniversalSearchLauncher.Core.Models;

public enum SearchCategory
{
    Application = 0,
    File = 1,
    Web = 2
}

public enum SearchActionType
{
    LaunchApplication,
    OpenFile,
    OpenFolder,
    RevealInExplorer,
    OpenUrl,
    SearchWeb
}

public enum LaunchMode
{
    Executable,
    Shortcut,
    Aumid,
    Url
}
