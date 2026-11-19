using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace UniversalSearchLauncher.Services;

public sealed class UpdateCheckResult
{
    public bool IsConfigured { get; init; }
    public bool UpdateAvailable { get; init; }
    public string Message { get; init; } = string.Empty;
    public Uri? DownloadUri { get; init; }
    public Version? LatestVersion { get; init; }
}

public sealed class UpdateService
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    public static Version CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0, 0);

    public static string CurrentVersionDisplay
    {
        get
        {
            var version = CurrentVersion;
            return $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    public static async Task<UpdateCheckResult> CheckAsync(string? feedUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(feedUrl))
        {
            return new UpdateCheckResult
            {
                IsConfigured = false,
                Message = $"Канал обновлений не задан.\n\nУстановлена версия {CurrentVersionDisplay}. " +
                          "Укажите URL манифеста обновлений в настройках, чтобы проверять новые версии."
            };
        }

        try
        {
            using var response = await Client.GetAsync(feedUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult
                {
                    IsConfigured = true,
                    Message = $"Сервер обновлений вернул ошибку {(int)response.StatusCode} ({response.ReasonPhrase})."
                };
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;

            var versionText = GetString(root, "version") ?? GetString(root, "latestVersion");
            var download = GetString(root, "url") ?? GetString(root, "downloadUrl");
            var notes = GetString(root, "notes") ?? GetString(root, "releaseNotes");

            if (!Version.TryParse(versionText, out var latest))
            {
                return new UpdateCheckResult
                {
                    IsConfigured = true,
                    Message = "Манифест обновлений не содержит корректного поля version."
                };
            }

            var updateAvailable = Normalize(latest) > Normalize(CurrentVersion);
            if (!updateAvailable)
            {
                return new UpdateCheckResult
                {
                    IsConfigured = true,
                    UpdateAvailable = false,
                    LatestVersion = latest,
                    Message = $"У вас установлена последняя версия: {CurrentVersionDisplay}."
                };
            }

            Uri? downloadUri = null;
            if (!string.IsNullOrWhiteSpace(download))
            {
                Uri.TryCreate(download, UriKind.Absolute, out downloadUri);
            }

            var message = $"Доступна версия {latest}.\n\nУстановлена: {CurrentVersionDisplay}.";
            if (!string.IsNullOrWhiteSpace(notes))
            {
                message += "\n\n" + notes.Trim();
            }

            return new UpdateCheckResult
            {
                IsConfigured = true,
                UpdateAvailable = true,
                LatestVersion = latest,
                DownloadUri = downloadUri,
                Message = message
            };
        }
        catch (OperationCanceledException)
        {
            return new UpdateCheckResult
            {
                IsConfigured = true,
                Message = "Проверка обновлений отменена."
            };
        }
        catch (Exception ex)
        {
            AppLog.Error("Не удалось проверить обновления.", ex);
            return new UpdateCheckResult
            {
                IsConfigured = true,
                Message = "Не удалось проверить обновления.\n\n" + ex.Message
            };
        }
    }

    private static Version Normalize(Version version)
    {
        return new Version(version.Major, version.Minor, Math.Max(0, version.Build));
    }

    private static string? GetString(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }
        }

        return null;
    }
}
