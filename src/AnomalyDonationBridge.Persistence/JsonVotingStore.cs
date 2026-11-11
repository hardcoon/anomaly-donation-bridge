using System.Text.Json;
using AnomalyDonationBridge.Core;

namespace AnomalyDonationBridge.Persistence;

public sealed class JsonVotingStore(AppPaths paths) : IVotingStore
{
    // Round history stays complete and readable by older versions. Avoid
    // indenting/writing it in tiny write-through blocks on each editor action.
    private static readonly JsonSerializerOptions StorageOptions = new(JsonDefaults.Options)
        { WriteIndented = false, DefaultBufferSize = 128 * 1024 };
    public string FilePath => Path.Combine(paths.RootDirectory, "voting.json");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<VotingDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        paths.EnsureExists();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(FilePath)) return new VotingDocument();
            try
            {
                await using var stream = File.OpenRead(FilePath);
                return await JsonSerializer.DeserializeAsync<VotingDocument>(stream, JsonDefaults.Options, cancellationToken)
                    .ConfigureAwait(false) ?? throw new InvalidDataException("Пустой файл voting.json.");
            }
            catch (JsonException exception)
            {
                // Never silently restore an older accounting snapshot: that could
                // lose votes or redraw a winner already sent to the game.
                throw new InvalidDataException("Файл voting.json повреждён. Сбор остановлен; исходный файл и .bak сохранены.", exception);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(VotingDocument document, CancellationToken cancellationToken = default)
    {
        paths.EnsureExists();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var temporary = FilePath + ".tmp";
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write,
                FileShare.None, 128 * 1024, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, document, StorageOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                // Retain the durable commit boundary before atomic replacement.
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, FilePath + ".bak");
            else File.Move(temporary, FilePath);
        }
        finally { _gate.Release(); }
    }
}
