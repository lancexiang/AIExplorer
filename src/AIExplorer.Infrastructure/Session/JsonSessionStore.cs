using System.Text.Json;
using AIExplorer.Core.Session;

namespace AIExplorer.Infrastructure.Session;

public sealed class JsonSessionStore : ISessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _storagePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonSessionStore()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AIExplorer");
        Directory.CreateDirectory(folder);
        _storagePath = Path.Combine(folder, "session.json");
    }

    public async Task<SessionState?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_storagePath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(_storagePath);
            return await JsonSerializer.DeserializeAsync<SessionState>(stream, JsonOptions, cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    public async Task SaveAsync(SessionState state, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var temp = _storagePath + ".tmp";
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken).ConfigureAwait(false);
            }

            File.Copy(temp, _storagePath, overwrite: true);
            File.Delete(temp);
        }
        finally
        {
            _gate.Release();
        }
    }
}
