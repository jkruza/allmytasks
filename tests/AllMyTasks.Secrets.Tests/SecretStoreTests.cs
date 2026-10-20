using System.Runtime.Versioning;
using System.Text;
using AllMyTasks.Secrets;

namespace AllMyTasks.Secrets.Tests;

public class IndexedSecretStoreTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Set_get_and_delete_one_field()
    {
        using var store = new IndexedSecretStore(new DictionaryBackend());

        Assert.Null(await store.GetAsync("src", "password", Ct));
        await store.SetAsync("src", "password", "hunter2", Ct);
        Assert.Equal("hunter2", await store.GetAsync("src", "password", Ct));
        await store.DeleteAsync("src", "password", Ct);
        Assert.Null(await store.GetAsync("src", "password", Ct));
    }

    [Fact]
    public async Task Deleting_a_source_removes_all_its_fields_and_nothing_else()
    {
        var backend = new DictionaryBackend();
        using var store = new IndexedSecretStore(backend);
        await store.SetAsync("a", "password", "1", Ct);
        await store.SetAsync("a", "url", "2", Ct);
        await store.SetAsync("b", "password", "3", Ct);

        await store.DeleteAsync("a", Ct);

        Assert.Null(await store.GetAsync("a", "password", Ct));
        Assert.Null(await store.GetAsync("a", "url", Ct));
        Assert.Equal("3", await store.GetAsync("b", "password", Ct));
        Assert.DoesNotContain(backend.Entries.Keys, k => k.StartsWith("a/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Overwriting_a_field_does_not_duplicate_the_index()
    {
        var backend = new DictionaryBackend();
        using var store = new IndexedSecretStore(backend);

        await store.SetAsync("a", "password", "1", Ct);
        await store.SetAsync("a", "password", "2", Ct);
        await store.DeleteAsync("a", Ct);

        Assert.Empty(backend.Entries);
    }

    [Fact]
    public async Task Deleting_the_last_field_removes_the_index_too()
    {
        var backend = new DictionaryBackend();
        using var store = new IndexedSecretStore(backend);
        await store.SetAsync("a", "password", "1", Ct);

        await store.DeleteAsync("a", "password", Ct);

        Assert.Empty(backend.Entries);
    }

    [Fact]
    public async Task A_damaged_index_does_not_prevent_deleting_the_source()
    {
        var backend = new DictionaryBackend();
        using var store = new IndexedSecretStore(backend);
        await store.SetAsync("a", "password", "1", Ct);
        backend.Entries["a/\u0001index"] = "not json";

        await store.DeleteAsync("a", Ct);

        Assert.DoesNotContain("a/\u0001index", backend.Entries.Keys);
    }

    [Theory]
    [InlineData("", "f")]
    [InlineData("s", "")]
    [InlineData("s", "bad\u0001field")]
    public async Task Rejects_invalid_names(string source, string field)
    {
        using var store = new IndexedSecretStore(new DictionaryBackend());

        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.SetAsync(source, field, "x", Ct));
    }
}

public class PinProtectedFileBackendTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    // Cheap settings so tests are fast; real ones come from Calibrate.
    private static readonly Argon2Parameters Cheap = new(8, 1, 1);

    private static PinProtectedFileBackend Create(string dir, ScriptedPrompt prompt, TimeProvider? clock = null) =>
        new(PinProtectedFileBackend.DefaultPath(dir), prompt, Cheap, clock);

    [Fact]
    public async Task Secrets_survive_a_restart_with_the_right_pin_and_are_not_in_the_file_as_plaintext()
    {
        using var dir = new TempDirectory();
        using (var first = Create(dir.Path, new ScriptedPrompt("123456")))
        {
            await first.SetAsync("src/password", "correct horse battery staple", Ct);
            await first.SetAsync("src/url", "https://example.com/private?token=abc", Ct);
        }

        var bytes = await File.ReadAllBytesAsync(PinProtectedFileBackend.DefaultPath(dir.Path), Ct);
        Assert.DoesNotContain("horse", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.DoesNotContain("token=abc", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);

        var prompt = new ScriptedPrompt("123456");
        using var second = Create(dir.Path, prompt);
        Assert.Equal("correct horse battery staple", await second.GetAsync("src/password", Ct));
        Assert.Equal("https://example.com/private?token=abc", await second.GetAsync("src/url", Ct));
        Assert.Equal([new PinRequest(false, 0)], prompt.Requests);
    }

    [Fact]
    public async Task The_pin_is_asked_once_per_run_not_per_operation()
    {
        using var dir = new TempDirectory();
        var prompt = new ScriptedPrompt("123456");
        using var backend = Create(dir.Path, prompt);

        await backend.SetAsync("a", "1", Ct);
        await backend.SetAsync("b", "2", Ct);
        _ = await backend.GetAsync("a", Ct);

        Assert.Equal([new PinRequest(true, 0)], prompt.Requests);
    }

    [Fact]
    public async Task A_wrong_pin_is_asked_again_with_the_attempt_count_and_the_right_one_then_works()
    {
        using var dir = new TempDirectory();
        using (var first = Create(dir.Path, new ScriptedPrompt("123456")))
        {
            await first.SetAsync("a", "secret", Ct);
        }

        var prompt = new ScriptedPrompt("000000", "111111", "123456");
        using var second = Create(dir.Path, prompt);

        Assert.Equal("secret", await second.GetAsync("a", Ct));
        Assert.Equal([0, 1, 2], prompt.Requests.Select(r => r.FailedAttempts));
    }

    [Fact]
    public async Task Cancelling_the_prompt_makes_the_store_unavailable_without_touching_the_file()
    {
        using var dir = new TempDirectory();
        using (var first = Create(dir.Path, new ScriptedPrompt("123456")))
        {
            await first.SetAsync("a", "secret", Ct);
        }

        var before = await File.ReadAllBytesAsync(PinProtectedFileBackend.DefaultPath(dir.Path), Ct);
        using var second = Create(dir.Path, new ScriptedPrompt("000000", null));

        await Assert.ThrowsAsync<SecretStoreUnavailableException>(() => second.GetAsync("a", Ct));

        Assert.Equal(before, await File.ReadAllBytesAsync(PinProtectedFileBackend.DefaultPath(dir.Path), Ct));
    }

    [Fact]
    public async Task Creating_a_new_file_with_a_cancelled_prompt_fails_cleanly()
    {
        using var dir = new TempDirectory();
        using var backend = Create(dir.Path, new ScriptedPrompt((string?)null));

        await Assert.ThrowsAsync<SecretStoreUnavailableException>(() => backend.SetAsync("a", "x", Ct));

        Assert.False(File.Exists(PinProtectedFileBackend.DefaultPath(dir.Path)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(25)]
    [InlineData(-1)]
    public async Task Tampering_with_the_header_or_ciphertext_is_detected(int offset)
    {
        using var dir = new TempDirectory();
        var path = PinProtectedFileBackend.DefaultPath(dir.Path);
        using (var first = Create(dir.Path, new ScriptedPrompt("123456")))
        {
            await first.SetAsync("a", "secret", Ct);
        }

        var bytes = await File.ReadAllBytesAsync(path, Ct);
        bytes[offset < 0 ? bytes.Length - 20 : offset] ^= 0x01;
        await File.WriteAllBytesAsync(path, bytes, Ct);
        using var second = Create(dir.Path, new ScriptedPrompt("123456", null));

        // Offset 0 breaks the magic number (format error); the others fail authentication like a wrong PIN.
        await Assert.ThrowsAnyAsync<SecretStoreException>(() => second.GetAsync("a", Ct));
    }

    [Fact]
    public async Task Each_write_uses_a_fresh_nonce()
    {
        using var dir = new TempDirectory();
        var path = PinProtectedFileBackend.DefaultPath(dir.Path);
        using var backend = Create(dir.Path, new ScriptedPrompt("123456"));

        await backend.SetAsync("a", "1", Ct);
        var first = (await File.ReadAllBytesAsync(path, Ct))[32..44];
        await backend.SetAsync("a", "1", Ct);
        var second = (await File.ReadAllBytesAsync(path, Ct))[32..44];

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task Changing_the_pin_re_encrypts_under_a_new_salt()
    {
        using var dir = new TempDirectory();
        var path = PinProtectedFileBackend.DefaultPath(dir.Path);
        byte[] oldSalt;
        using (var first = Create(dir.Path, new ScriptedPrompt("123456")))
        {
            await first.SetAsync("a", "secret", Ct);
            oldSalt = (await File.ReadAllBytesAsync(path, Ct))[20..36];
            await first.ChangePinAsync("a-longer-passphrase", Ct);
        }

        Assert.NotEqual(oldSalt, (await File.ReadAllBytesAsync(path, Ct))[20..36]);
        using var withOld = Create(dir.Path, new ScriptedPrompt("123456", null));
        await Assert.ThrowsAsync<SecretStoreUnavailableException>(() => withOld.GetAsync("a", Ct));
        using var withNew = Create(dir.Path, new ScriptedPrompt("a-longer-passphrase"));
        Assert.Equal("secret", await withNew.GetAsync("a", Ct));
    }

    [Fact]
    public async Task Forgot_pin_discards_the_secrets_and_starts_over()
    {
        using var dir = new TempDirectory();
        var path = PinProtectedFileBackend.DefaultPath(dir.Path);
        using (var first = Create(dir.Path, new ScriptedPrompt("123456")))
        {
            await first.SetAsync("a", "secret", Ct);
            await first.ResetAsync(Ct);
        }

        Assert.False(File.Exists(path));
        var prompt = new ScriptedPrompt("654321");
        using var again = Create(dir.Path, prompt);
        Assert.Null(await again.GetAsync("a", Ct));
        Assert.True(prompt.Requests[0].IsNewFile);
    }

    [Fact]
    public async Task Repeated_wrong_pins_are_slowed_down_after_five_tries_with_a_capped_backoff()
    {
        using var dir = new TempDirectory();
        using (var first = Create(dir.Path, new ScriptedPrompt("123456")))
        {
            await first.SetAsync("a", "secret", Ct);
        }

        var clock = new InstantClock();
        var wrong = Enumerable.Repeat("000000", 9).Append("123456").ToArray();
        using var second = Create(dir.Path, new ScriptedPrompt(wrong), clock);

        Assert.Equal("secret", await second.GetAsync("a", Ct));
        Assert.Equal([1, 2, 4, 8, 16], clock.Delays.Select(d => (int)d.TotalSeconds));
    }

    [Fact]
    public async Task Deleting_a_missing_account_does_not_prompt_or_create_a_file()
    {
        using var dir = new TempDirectory();
        var prompt = new ScriptedPrompt("123456");
        using var backend = Create(dir.Path, prompt);

        await backend.DeleteAsync("nothing", Ct);

        Assert.Empty(prompt.Requests);
        Assert.False(File.Exists(PinProtectedFileBackend.DefaultPath(dir.Path)));
    }

    [Fact(Skip = "Linux only", SkipUnless = nameof(Platform.IsLinux), SkipType = typeof(Platform))]
    [UnsupportedOSPlatform("windows")]
    public async Task The_file_is_only_readable_by_its_owner()
    {
        using var dir = new TempDirectory();
        using var backend = Create(dir.Path, new ScriptedPrompt("123456"));
        await backend.SetAsync("a", "x", Ct);

        var mode = File.GetUnixFileMode(PinProtectedFileBackend.DefaultPath(dir.Path));

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    [Fact]
    public async Task Works_through_the_indexed_store_end_to_end()
    {
        using var dir = new TempDirectory();
        using var backend = Create(dir.Path, new ScriptedPrompt("123456"));
        using var store = new IndexedSecretStore(backend);

        await store.SetAsync("src", "password", "p", Ct);
        await store.SetAsync("src", "url", "u", Ct);
        await store.DeleteAsync("src", Ct);

        Assert.Null(await store.GetAsync("src", "password", Ct));
    }

    [Fact]
    public void Calibration_raises_the_cost_and_stops()
    {
        var tuned = Argon2Parameters.Calibrate(TimeSpan.FromMilliseconds(1), new Argon2Parameters(8, 1, 1));

        Assert.True(tuned.Iterations >= 1);
        Assert.Equal(8, tuned.MemoryKiB);
    }
}

public class LinuxSecretToolBackendTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    /// <summary>A stand-in for secret-tool that keeps secrets in files, so the argument handling can be tested.</summary>
    [UnsupportedOSPlatform("windows")]
    private static string FakeTool(string dir, string behaviour = "ok")
    {
        var store = Path.Combine(dir, "store");
        Directory.CreateDirectory(store);
        var script = Path.Combine(dir, "secret-tool");
        File.WriteAllText(script, $$"""
            #!/bin/sh
            mode={{behaviour}}
            cmd="$1"; shift
            if [ "$mode" = "no-service" ]; then echo "Cannot autolaunch D-Bus without X11 \$DISPLAY" >&2; exit 1; fi
            if [ "$mode" = "broken" ]; then echo "boom" >&2; exit 2; fi
            # Arguments after the command: [--label=x] service S account A
            while [ "$1" != "account" ] && [ $# -gt 0 ]; do shift; done
            account=$(printf '%s' "$2" | od -An -tx1 | tr -d ' \n')
            file="{{store}}/$account"
            case "$cmd" in
              store) cat > "$file" ;;
              lookup) if [ -f "$file" ]; then cat "$file"; else exit 1; fi ;;
              clear) rm -f "$file" ;;
            esac
            """.Replace("\r\n", "\n", StringComparison.Ordinal));
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    [Fact(Skip = "Linux only", SkipUnless = nameof(Platform.IsLinux), SkipType = typeof(Platform))]
    [UnsupportedOSPlatform("windows")]
    public async Task Stores_reads_and_deletes_through_the_tool_with_secrets_on_stdin()
    {
        using var dir = new TempDirectory();
        var backend = new LinuxSecretToolBackend(FakeTool(dir.Path));

        Assert.Null(await backend.GetAsync("src/password", Ct));
        await backend.SetAsync("src/password", "multi\nline ünïcode secret", Ct);
        Assert.Equal("multi\nline ünïcode secret", await backend.GetAsync("src/password", Ct));
        await backend.DeleteAsync("src/password", Ct);
        Assert.Null(await backend.GetAsync("src/password", Ct));
    }

    [Fact(Skip = "Linux only", SkipUnless = nameof(Platform.IsLinux), SkipType = typeof(Platform))]
    [UnsupportedOSPlatform("windows")]
    public async Task Availability_distinguishes_a_missing_entry_from_a_missing_service()
    {
        using var ok = new TempDirectory();
        using var noService = new TempDirectory();

        Assert.True(await LinuxSecretToolBackend.IsAvailableAsync(FakeTool(ok.Path), Ct));
        Assert.False(await LinuxSecretToolBackend.IsAvailableAsync(FakeTool(noService.Path, "no-service"), Ct));
        Assert.False(await LinuxSecretToolBackend.IsAvailableAsync(Path.Combine(ok.Path, "does-not-exist"), Ct));
    }

    [Fact(Skip = "Linux only", SkipUnless = nameof(Platform.IsLinux), SkipType = typeof(Platform))]
    [UnsupportedOSPlatform("windows")]
    public async Task Failures_are_reported_without_the_tool_output_or_the_secret()
    {
        using var dir = new TempDirectory();
        var backend = new LinuxSecretToolBackend(FakeTool(dir.Path, "broken"));

        var ex = await Assert.ThrowsAsync<SecretStoreException>(() => backend.SetAsync("src/password", "hunter2", Ct));

        Assert.DoesNotContain("hunter2", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("boom", ex.Message, StringComparison.Ordinal);
    }
}

public class SecretStoreFactoryTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    [Fact(Skip = "Linux only", SkipUnless = nameof(Platform.IsLinux), SkipType = typeof(Platform))]
    public async Task Without_a_keyring_the_user_must_agree_before_anything_is_stored()
    {
        if (await LinuxSecretToolBackend.IsAvailableAsync(cancellationToken: Ct))
        {
            Assert.Skip("A system keyring is available here, so the PIN fallback is not used.");
        }

        using var dir = new TempDirectory();
        var prompt = new ScriptedPrompt("123456") { AllowFallback = false };
        using var factory = new SecretStoreFactory(dir.Path, prompt);

        await Assert.ThrowsAsync<SecretStoreUnavailableException>(() => factory.SetAsync("s", "f", "v", Ct));

        Assert.False(File.Exists(PinProtectedFileBackend.DefaultPath(dir.Path)));
        Assert.Null(factory.Kind);
    }

    [Fact(Skip = "Linux only", SkipUnless = nameof(Platform.IsLinux), SkipType = typeof(Platform))]
    public async Task Without_a_keyring_and_with_consent_a_pin_protected_file_is_used()
    {
        if (await LinuxSecretToolBackend.IsAvailableAsync(cancellationToken: Ct))
        {
            Assert.Skip("A system keyring is available here, so the PIN fallback is not used.");
        }

        using var dir = new TempDirectory();
        using var factory = new SecretStoreFactory(dir.Path, new ScriptedPrompt("123456"), new Argon2Parameters(8, 1, 1));

        await factory.SetAsync("s", "f", "v", Ct);

        Assert.Equal(SecretBackendKind.PinProtectedFile, factory.Kind);
        Assert.Equal("v", await factory.GetAsync("s", "f", Ct));
        Assert.NotNull(factory.PinFile);
    }
}
