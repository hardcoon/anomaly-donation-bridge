using System.Security.Cryptography;
using System.Text.Json;
using AnomalyDonationBridge.Core;

namespace AnomalyDonationBridge.Persistence;

public sealed class JsonVotingStore(AppPaths paths) : IChatVotingStore
{
    private const int CheckpointRecords = 4096;
    private const long CheckpointBytes = 8 * 1024 * 1024;
    private const int MaximumRecordBytes = 16 * 1024 * 1024;
    private const int MaximumJournalBytes = 64 * 1024 * 1024;
    private static readonly JsonSerializerOptions StorageOptions = new(JsonDefaults.Options)
        { WriteIndented = false, DefaultBufferSize = 128 * 1024 };
    public string FilePath => Path.Combine(paths.RootDirectory, "voting.json");
    public string JournalPath => Path.Combine(paths.RootDirectory, "voting-chat.wal");
    public string MigrationBackupPath => Path.Combine(paths.RootDirectory, "voting.before-chat-v8.json");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _journalRecords;
    private long _journalBytes;
    private bool _journalResetPending, _writeFaulted;

    private sealed record JournalEntry(int Version, string CheckpointId, string Payload, string Sha256);

    public async Task<VotingDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        paths.EnsureExists();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(FilePath))
            {
                if (File.Exists(JournalPath) && new FileInfo(JournalPath).Length > 0)
                    throw new InvalidDataException("Не найден voting.json для журнала голосов чата. Автоматический сброс запрещён.");
                return new VotingDocument();
            }
            VotingDocument document;
            try
            {
                await using var stream = File.OpenRead(FilePath);
                document = await JsonSerializer.DeserializeAsync<VotingDocument>(stream, JsonDefaults.Options, cancellationToken)
                    .ConfigureAwait(false) ?? throw new InvalidDataException("Пустой файл voting.json.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("Файл voting.json повреждён. Сбор остановлен; исходный файл и .bak сохранены.", exception);
            }
            if (document.SchemaVersion < 8 && !File.Exists(MigrationBackupPath))
                File.Copy(FilePath, MigrationBackupPath, overwrite: false);
            await ReplayJournalAsync(document, cancellationToken).ConfigureAwait(false);
            _writeFaulted = false;
            return document;
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(VotingDocument document, CancellationToken cancellationToken = default)
    {
        paths.EnsureExists();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { ThrowIfWriteFaulted(); await CheckpointAsync(document, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task SaveChatAsync(VotingDocument document, VotingChatCommit commit, CancellationToken token = default)
    {
        paths.EnsureExists();
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfWriteFaulted();
            if (!File.Exists(FilePath) || string.IsNullOrEmpty(document.ChatJournalId) || _journalResetPending ||
                _journalRecords >= CheckpointRecords || _journalBytes >= CheckpointBytes)
            {
                // This snapshot already includes the new vote. Never append first
                // and then report a checkpoint failure for an acknowledged vote.
                await CheckpointAsync(document, token).ConfigureAwait(false);
                return;
            }
            var payload = JsonSerializer.SerializeToUtf8Bytes(commit, StorageOptions);
            var entry = new JournalEntry(1, document.ChatJournalId, Convert.ToBase64String(payload),
                Convert.ToHexString(SHA256.HashData(payload)));
            var bytes = JsonSerializer.SerializeToUtf8Bytes(entry, StorageOptions);
            if (bytes.Length > MaximumRecordBytes) throw new InvalidDataException("Слишком большая запись журнала голосов чата.");
            await using var stream = new FileStream(JournalPath, FileMode.OpenOrCreate, FileAccess.Write,
                FileShare.Read, 128 * 1024, FileOptions.Asynchronous);
            var start = stream.Length;
            stream.Position = start;
            try
            {
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                await stream.WriteAsync(new byte[] { (byte)'\n' }, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            catch
            {
                // A failed append must not become an accepted vote on retry.
                try { stream.SetLength(start); stream.Flush(flushToDisk: true); }
                catch { _writeFaulted = true; }
                throw;
            }
            _journalRecords++;
            _journalBytes = start + bytes.Length + 1;
        }
        finally { _gate.Release(); }
    }

    private async Task CheckpointAsync(VotingDocument document, CancellationToken token)
    {
        var previousId = document.ChatJournalId;
        document.ChatJournalId = Guid.NewGuid().ToString("N");
        var temporary = FilePath + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write,
                FileShare.None, 128 * 1024, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, document, StorageOptions, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, FilePath + ".bak");
            else File.Move(temporary, FilePath);
        }
        catch { document.ChatJournalId = previousId; throw; }
        // A committed checkpoint supersedes the old journal even if cleanup is
        // blocked, or a crash happens before deletion. Epochs also cover removal
        // of entire rounds: old records must not resurrect them on restart.
        try
        {
            File.Delete(JournalPath);
            _journalBytes = 0; _journalRecords = 0; _journalResetPending = false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { _journalResetPending = true; }
    }

    private async Task ReplayJournalAsync(VotingDocument document, CancellationToken token)
    {
        _journalBytes = 0; _journalRecords = 0; _journalResetPending = false;
        if (!File.Exists(JournalPath)) return;
        if (new FileInfo(JournalPath).Length > 0 &&
            (document.SchemaVersion != 8 || !Guid.TryParseExact(document.ChatJournalId, "N", out _)))
            throw new InvalidDataException("Файл voting.json не содержит подходящей контрольной точки для журнала голосов чата. Исходные данные сохранены.");
        if (new FileInfo(JournalPath).Length > MaximumJournalBytes)
            throw new InvalidDataException("Превышен допустимый размер журнала голосов чата. Исходные данные сохранены.");
        var bytes = await File.ReadAllBytesAsync(JournalPath, token).ConfigureAwait(false);
        var start = 0;
        try
        {
            for (var i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] != (byte)'\n') continue;
                if (i - start > MaximumRecordBytes) throw new InvalidDataException("Слишком большая запись журнала голосов чата.");
                var entry = JsonSerializer.Deserialize<JournalEntry>(bytes.AsSpan(start, i - start), StorageOptions)
                    ?? throw new InvalidDataException("Пустая запись журнала голосов чата.");
                if (entry.Version != 1 || !Guid.TryParseExact(entry.CheckpointId, "N", out _) ||
                    entry.Payload is null || entry.Sha256 is null)
                    throw new InvalidDataException("Неизвестный формат журнала голосов чата.");
                var payload = Convert.FromBase64String(entry.Payload);
                if (!string.Equals(Convert.ToHexString(SHA256.HashData(payload)), entry.Sha256, StringComparison.Ordinal))
                    throw new InvalidDataException("Неверная контрольная сумма журнала голосов чата.");
                var commit = JsonSerializer.Deserialize<VotingChatCommit>(payload, StorageOptions)
                    ?? throw new InvalidDataException("Пустой голос в журнале чата.");
                if (entry.CheckpointId == document.ChatJournalId) commit.Apply(document);
                _journalRecords++;
                start = i + 1;
            }
        }
        catch (Exception error) when (error is JsonException or FormatException or ArgumentException)
        { throw new InvalidDataException("Журнал голосов чата повреждён. Исходные файлы сохранены; сбор остановлен.", error); }
        // Only an incomplete final append can be discarded. A damaged complete
        // record above fails closed instead of restoring stale accounting.
        if (start < bytes.Length)
        {
            await using var stream = new FileStream(JournalPath, FileMode.Open, FileAccess.Write, FileShare.None);
            stream.SetLength(start); stream.Flush(flushToDisk: true);
        }
        _journalBytes = start;
    }

    private void ThrowIfWriteFaulted()
    {
        if (_writeFaulted) throw new IOException("Не удалось восстановить журнал после ошибки записи. Нужен перезапуск программы.");
    }
}
