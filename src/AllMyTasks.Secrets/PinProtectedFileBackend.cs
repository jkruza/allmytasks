using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Konscious.Security.Cryptography;

namespace AllMyTasks.Secrets;

/// <summary>Argon2id cost settings, stored in the file header so they can be raised later.</summary>
/// <param name="MemoryKiB">Memory in KiB.</param>
/// <param name="Iterations">Passes over the memory.</param>
/// <param name="Parallelism">Lanes.</param>
public sealed record Argon2Parameters(int MemoryKiB, int Iterations, int Parallelism)
{
    /// <summary>Default starting point; <see cref="Calibrate"/> raises the iterations to reach roughly half a second.</summary>
    public static Argon2Parameters Default { get; } = new(64 * 1024, 3, 2);

    /// <summary>Raises the iteration count until one derivation takes about <paramref name="target"/> on this machine.</summary>
    public static Argon2Parameters Calibrate(TimeSpan target, Argon2Parameters? start = null)
    {
        var current = start ?? Default;
        var salt = RandomNumberGenerator.GetBytes(16);
        for (var attempt = 0; attempt < 5 && current.Iterations < 32; attempt++)
        {
            var sw = Stopwatch.StartNew();
            PinProtectedFileBackend.DeriveKey("calibration", salt, current);
            if (sw.Elapsed >= target)
            {
                break;
            }

            current = current with { Iterations = current.Iterations * 2 };
        }

        return current;
    }
}

/// <summary>
/// The fallback when no OS keyring exists (typically Linux without a Secret Service): one file,
/// <c>secrets.enc</c>, encrypted with AES-256-GCM under a key derived from a PIN with Argon2id.
/// </summary>
/// <remarks>
/// A 6-digit PIN has only a million possibilities, so anyone who copies the file can brute-force it offline.
/// This protects against casual access and leaked backups, not a determined attacker. There is no PIN recovery:
/// <see cref="ResetAsync"/> discards the secrets.
/// </remarks>
public sealed class PinProtectedFileBackend : ISecretBackend, IDisposable
{
    private static readonly byte[] Magic = "AMTSEC1\0"u8.ToArray();
    internal const int SaltSize = 16;
    internal const int NonceSize = 12;
    internal const int TagSize = 16;
    internal const int KeySize = 32;
    internal const int HeaderSize = 8 + 12 + SaltSize + NonceSize;
    internal const int FreeAttempts = 5;

    private readonly string _path;
    private readonly ISecretUserPrompt _prompt;
    private readonly Argon2Parameters _newFileParameters;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, string>? _secrets;
    private byte[]? _key;
    private byte[]? _salt;
    private Argon2Parameters? _parameters;

    public PinProtectedFileBackend(string path, ISecretUserPrompt prompt, Argon2Parameters? parameters = null, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _prompt = prompt ?? throw new ArgumentNullException(nameof(prompt));
        _newFileParameters = parameters ?? Argon2Parameters.Calibrate(TimeSpan.FromMilliseconds(500));
        _clock = clock ?? TimeProvider.System;
    }

    public string Name => "PIN-protected file";

    /// <summary>The default location inside the data directory.</summary>
    public static string DefaultPath(string dataDirectory) => Path.Combine(dataDirectory, "secrets.enc");

    public async Task<string?> GetAsync(string account, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var secrets = await UnlockAsync(cancellationToken).ConfigureAwait(false);
            return secrets.TryGetValue(account, out var value) ? value : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetAsync(string account, string secret, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var secrets = await UnlockAsync(cancellationToken).ConfigureAwait(false);
            secrets[account] = secret;
            await SaveAsync(secrets, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(string account, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path) && _secrets is null)
            {
                return;
            }

            var secrets = await UnlockAsync(cancellationToken).ConfigureAwait(false);
            if (secrets.Remove(account))
            {
                await SaveAsync(secrets, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Re-encrypts the file under a new random salt and a key from <paramref name="newPin"/>.</summary>
    public async Task ChangePinAsync(string newPin, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(newPin);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var secrets = await UnlockAsync(cancellationToken).ConfigureAwait(false);
            ZeroKey();
            _salt = RandomNumberGenerator.GetBytes(SaltSize);
            _parameters = _newFileParameters;
            _key = DeriveKey(newPin, _salt, _parameters);
            await SaveAsync(secrets, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>"Forgot PIN": discards the file and every secret in it. Calendar and task data are unaffected.</summary>
    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            File.Delete(_path);
            _secrets = null;
            ZeroKey();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        ZeroKey();
        _secrets = null;
        _gate.Dispose();
    }

    internal static byte[] DeriveKey(string pin, byte[] salt, Argon2Parameters parameters)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(pin))
        {
            Salt = salt,
            MemorySize = parameters.MemoryKiB,
            Iterations = parameters.Iterations,
            DegreeOfParallelism = parameters.Parallelism,
        };
        return argon.GetBytes(KeySize);
    }

    private async Task<Dictionary<string, string>> UnlockAsync(CancellationToken cancellationToken)
    {
        if (_secrets is not null)
        {
            return _secrets;
        }

        if (!File.Exists(_path))
        {
            var pin = await _prompt.RequestPinAsync(new PinRequest(true, 0), cancellationToken).ConfigureAwait(false)
                ?? throw new SecretStoreUnavailableException("A PIN is needed to protect stored passwords.");
            _salt = RandomNumberGenerator.GetBytes(SaltSize);
            _parameters = _newFileParameters;
            _key = DeriveKey(pin, _salt, _parameters);
            _secrets = [];
            return _secrets;
        }

        var bytes = await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false);
        var (parameters, salt, nonce, cipher, tag) = ParseFile(bytes);
        var failed = 0;
        while (true)
        {
            var pin = await _prompt.RequestPinAsync(new PinRequest(false, failed), cancellationToken).ConfigureAwait(false)
                ?? throw new SecretStoreUnavailableException("The PIN was not entered, so stored passwords are unavailable.");
            var key = DeriveKey(pin, salt, parameters);
            try
            {
                var plain = new byte[cipher.Length];
                using var gcm = new AesGcm(key, TagSize);
                gcm.Decrypt(nonce, cipher, tag, plain, bytes.AsSpan(0, HeaderSize));
                _secrets = JsonSerializer.Deserialize<Dictionary<string, string>>(plain) ?? [];
                CryptographicOperations.ZeroMemory(plain);
                _key = key;
                _salt = salt;
                _parameters = parameters;
                return _secrets;
            }
            catch (CryptographicException)
            {
                CryptographicOperations.ZeroMemory(key);
                failed++;

                // Only slows the UI down; it does not protect the file (see the class remarks).
                if (failed >= FreeAttempts)
                {
                    var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, failed - FreeAttempts)));
                    await Task.Delay(delay, _clock, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task SaveAsync(Dictionary<string, string> secrets, CancellationToken cancellationToken)
    {
        var key = _key ?? throw new InvalidOperationException("Secret store is locked.");
        var plain = JsonSerializer.SerializeToUtf8Bytes(secrets);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var parameters = _parameters!;

        var file = new byte[HeaderSize + plain.Length + TagSize];
        Magic.CopyTo(file, 0);
        BitConverter.TryWriteBytes(file.AsSpan(8, 4), parameters.MemoryKiB);
        BitConverter.TryWriteBytes(file.AsSpan(12, 4), parameters.Iterations);
        BitConverter.TryWriteBytes(file.AsSpan(16, 4), parameters.Parallelism);
        _salt!.CopyTo(file, 20);
        nonce.CopyTo(file, 20 + SaltSize);

        using (var gcm = new AesGcm(key, TagSize))
        {
            var cipher = file.AsSpan(HeaderSize, plain.Length);
            var tag = file.AsSpan(HeaderSize + plain.Length, TagSize);
            gcm.Encrypt(nonce, plain, cipher, tag, file.AsSpan(0, HeaderSize));
        }

        CryptographicOperations.ZeroMemory(plain);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);

        // Write to a temp file and move it into place so a crash never leaves a half-written file.
        var temp = _path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        await using (var stream = new FileStream(temp, options))
        {
            await stream.WriteAsync(file, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temp, _path, overwrite: true);
    }

    private static (Argon2Parameters Parameters, byte[] Salt, byte[] Nonce, byte[] Cipher, byte[] Tag) ParseFile(byte[] bytes)
    {
        if (bytes.Length < HeaderSize + TagSize || !bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new SecretStoreException("The secrets file is not in a recognised format.");
        }

        var parameters = new Argon2Parameters(
            BitConverter.ToInt32(bytes, 8),
            BitConverter.ToInt32(bytes, 12),
            BitConverter.ToInt32(bytes, 16));
        if (parameters.MemoryKiB < 8 || parameters.Iterations < 1 || parameters.Parallelism < 1 || parameters.MemoryKiB > 4 * 1024 * 1024)
        {
            throw new SecretStoreException("The secrets file has invalid key settings.");
        }

        var cipherLength = bytes.Length - HeaderSize - TagSize;
        return (
            parameters,
            bytes[20..(20 + SaltSize)],
            bytes[(20 + SaltSize)..HeaderSize],
            bytes[HeaderSize..(HeaderSize + cipherLength)],
            bytes[(HeaderSize + cipherLength)..]);
    }

    private void ZeroKey()
    {
        if (_key is not null)
        {
            CryptographicOperations.ZeroMemory(_key);
            _key = null;
        }
    }
}
