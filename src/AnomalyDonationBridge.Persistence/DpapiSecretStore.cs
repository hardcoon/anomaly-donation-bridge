using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace AnomalyDonationBridge.Persistence;

public sealed class DpapiSecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AnomalyDonationBridge|secrets|v1");
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public DpapiSecretStore(AppPaths paths) => _paths = paths;

    public async Task<SecretBundle> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_paths.SecretsFile))
        {
            return new SecretBundle();
        }

        var base64 = await File.ReadAllTextAsync(_paths.SecretsFile, cancellationToken).ConfigureAwait(false);
        try
        {
            var cipher = Convert.FromBase64String(base64.Trim());
            var plain = Dpapi.Unprotect(cipher, Entropy);
            try
            {
                var secrets = JsonSerializer.Deserialize<SecretBundle>(plain, JsonDefaults.JsonLinesOptions) ?? new SecretBundle();
                using var document = JsonDocument.Parse(plain);
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.EnumerateObject().Any(property =>
                        property.Name.Equals("donatePayApiKey", StringComparison.OrdinalIgnoreCase) ||
                        property.Name.Equals("hasDonatePayKey", StringComparison.OrdinalIgnoreCase)))
                {
                    // Remove only retired provider fields by re-encrypting the
                    // current credentials. Never clear the other integrations.
                    await SaveAsync(secrets, cancellationToken).ConfigureAwait(false);
                }
                return secrets;
            }
            finally
            {
                Array.Clear(plain);
            }
        }
        catch (Exception exception) when (exception is FormatException or JsonException or Win32Exception)
        {
            throw new InvalidDataException("Не удалось расшифровать secrets.dpapi для текущего пользователя Windows.", exception);
        }
    }

    public async Task SaveAsync(SecretBundle secrets, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _paths.EnsureExists();
            var plain = JsonSerializer.SerializeToUtf8Bytes(secrets, JsonDefaults.JsonLinesOptions);
            try
            {
                var cipher = Dpapi.Protect(plain, Entropy);
                try
                {
                    var temporary = _paths.SecretsFile + ".tmp";
                    await File.WriteAllTextAsync(temporary, Convert.ToBase64String(cipher), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                    File.Move(temporary, _paths.SecretsFile, true);
                }
                finally { Array.Clear(cipher); }
            }
            finally { Array.Clear(plain); }
        }
        finally { _saveGate.Release(); }
    }

    public void Delete()
    {
        if (File.Exists(_paths.SecretsFile))
        {
            File.Delete(_paths.SecretsFile);
        }
    }

    private static class Dpapi
    {
        private const int CryptProtectUiForbidden = 0x1;

        public static byte[] Protect(byte[] plain, byte[] entropy) => Transform(plain, entropy, protect: true);
        public static byte[] Unprotect(byte[] cipher, byte[] entropy) => Transform(cipher, entropy, protect: false);

        private static byte[] Transform(byte[] input, byte[] entropy, bool protect)
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("DPAPI доступен только в Windows.");
            }

            var inputBlob = DataBlob.FromBytes(input);
            var entropyBlob = DataBlob.FromBytes(entropy);
            DataBlob outputBlob = default;
            try
            {
                var success = protect
                    ? CryptProtectData(ref inputBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out outputBlob)
                    : CryptUnprotectData(ref inputBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out outputBlob);
                if (!success)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                var result = new byte[outputBlob.Size];
                Marshal.Copy(outputBlob.Data, result, 0, result.Length);
                return result;
            }
            finally
            {
                inputBlob.FreeHGlobal();
                entropyBlob.FreeHGlobal();
                if (outputBlob.Data != IntPtr.Zero)
                {
                    LocalFree(outputBlob.Data);
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int Size;
            public IntPtr Data;

            public static DataBlob FromBytes(byte[] bytes)
            {
                var data = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, data, bytes.Length);
                return new DataBlob { Size = bytes.Length, Data = data };
            }

            public void FreeHGlobal()
            {
                if (Data != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(Data);
                    Data = IntPtr.Zero;
                }
            }
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptProtectData(
            ref DataBlob dataIn,
            string? description,
            ref DataBlob optionalEntropy,
            IntPtr reserved,
            IntPtr promptStruct,
            int flags,
            out DataBlob dataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptUnprotectData(
            ref DataBlob dataIn,
            IntPtr description,
            ref DataBlob optionalEntropy,
            IntPtr reserved,
            IntPtr promptStruct,
            int flags,
            out DataBlob dataOut);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}
