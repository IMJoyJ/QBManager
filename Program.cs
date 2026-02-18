using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NLua;

namespace QBManager;

class Program
{
    static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("╔══════════════════════════════════════╗");
        Console.WriteLine("║       QBManager - qBittorrent        ║");
        Console.WriteLine("║        Lua Script Manager            ║");
        Console.WriteLine("╚══════════════════════════════════════╝");
        Console.WriteLine();

        // ── Load config ──
        var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        if (!File.Exists(configPath))
        {
            // Fallback: try working directory
            configPath = Path.Combine(Directory.GetCurrentDirectory(), "config.json");
        }
        if (!File.Exists(configPath))
        {
            Console.Error.WriteLine("[Error] config.json not found.");
            return 1;
        }

        AppConfig config;
        try
        {
            var json = await File.ReadAllTextAsync(configPath);
            config = JsonSerializer.Deserialize<AppConfig>(json)
                     ?? throw new InvalidOperationException("Deserialized config is null.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Error] Failed to load config.json: {ex.Message}");
            return 1;
        }

        Console.WriteLine($"[Config] Server: {config.Server.BaseUrl}");
        Console.WriteLine($"[Config] Scripts: {string.Join(", ", config.Scripts)}");
        Console.WriteLine($"[Config] Script interval: {config.Settings.ScriptIntervalSeconds}s, " +
                          $"Loop interval: {config.Settings.LoopIntervalSeconds}s, " +
                          $"Max retries: {config.Settings.MaxRetryAttempts}");
        Console.WriteLine();

        // ── Setup HttpClient with CookieContainer ──
        var cookies = new CookieContainer();
        var handler = new HttpClientHandler
        {
            CookieContainer = cookies,
            UseCookies = true
        };

        var services = new ServiceCollection();
        services.AddHttpClient("qb")
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        var serviceProvider = services.BuildServiceProvider();

        var httpFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();
        var httpClient = httpFactory.CreateClient("qb");

        // ── Create QBClient ──
        var qb = new QBClient(httpClient, config.Server, cookies);

        // ── Login ──
        Console.WriteLine("[Main] Logging in to qBittorrent WebUI...");
        if (!await qb.LoginAsync())
        {
            Console.Error.WriteLine("[Main] Failed to login. Exiting.");
            return 1;
        }
        Console.WriteLine();

        // ── Graceful shutdown via Ctrl+C ──
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Console.WriteLine("\n[Main] Shutdown requested...");
        };

        // ── Resolve scripts directory ──
        var scriptBaseDir = Path.GetDirectoryName(configPath) ?? Directory.GetCurrentDirectory();

        // ── Main loop ──
        var loopCount = 0;
        while (!cts.Token.IsCancellationRequested)
        {
            loopCount++;
            Console.WriteLine($"════════ Loop #{loopCount} started ════════");

            for (int i = 0; i < config.Scripts.Length; i++)
            {
                if (cts.Token.IsCancellationRequested) break;

                var scriptName = config.Scripts[i];
                var scriptPath = Path.IsPathRooted(scriptName)
                    ? scriptName
                    : Path.Combine(scriptBaseDir, scriptName);

                if (!File.Exists(scriptPath))
                {
                    Console.Error.WriteLine($"[Script] File not found: {scriptPath}, skipping.");
                    continue;
                }

                var success = false;
                var attempt = 0;
                var maxAttempts = config.Settings.MaxRetryAttempts;

                while (!success && attempt <= maxAttempts)
                {
                    if (cts.Token.IsCancellationRequested) break;

                    if (attempt > 0)
                    {
                        Console.WriteLine($"[Script] Retry #{attempt}/{maxAttempts} for: {scriptName}");
                    }
                    else
                    {
                        Console.WriteLine($"[Script] Executing ({i + 1}/{config.Scripts.Length}): {scriptName}");
                    }

                    var returnCode = ExecuteScript(scriptPath, qb);

                    switch (returnCode)
                    {
                        case 1:
                            Console.WriteLine($"[Script] ✓ {scriptName} completed successfully.");
                            success = true;
                            break;
                        case 0:
                            attempt++;
                            if (attempt > maxAttempts)
                            {
                                Console.Error.WriteLine($"[Script] ✗ {scriptName} failed after {maxAttempts} retries.");
                            }
                            else
                            {
                                Console.WriteLine($"[Script] ⚠ {scriptName} returned 0 (retry needed).");
                            }
                            break;
                        case 2:
                            Console.Error.WriteLine($"[Script] ✗ {scriptName} returned 2 (no retry), skipping.");
                            success = true; // treat as "handled" to move on
                            break;
                        default:
                            Console.Error.WriteLine($"[Script] ✗ {scriptName} returned unexpected code: {returnCode}, treating as no-retry failure.");
                            success = true;
                            break;
                    }
                }

                // Wait between scripts (unless last or cancelled)
                if (i < config.Scripts.Length - 1 && !cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(config.Settings.ScriptIntervalSeconds), cts.Token);
                    }
                    catch (OperationCanceledException) { break; }
                }
            }

            Console.WriteLine($"════════ Loop #{loopCount} finished ════════");
            Console.WriteLine();

            // Wait between loops
            if (!cts.Token.IsCancellationRequested)
            {
                Console.WriteLine($"[Main] Waiting {config.Settings.LoopIntervalSeconds}s before next loop...");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(config.Settings.LoopIntervalSeconds), cts.Token);
                }
                catch (OperationCanceledException) { break; }
            }
        }

        Console.WriteLine("[Main] QBManager stopped.");
        return 0;
    }

    /// <summary>
    /// Execute a Lua script in a fresh NLua environment.
    /// Returns the script's integer return code (1=success, 0=retry, 2=no-retry).
    /// Returns 2 on any unrecoverable host-side error.
    /// </summary>
    private static int ExecuteScript(string scriptPath, QBClient qb)
    {
        NLua.Lua? lua = null;
        try
        {
            lua = new NLua.Lua();
            lua.State.Encoding = System.Text.Encoding.UTF8;

            // Open standard Lua libraries
            lua.LoadCLRPackage();

            // Register QBClient as global "qb"
            qb.BindLua(lua);
            lua["qb"] = qb;

            // Override print to go through Console
            lua.RegisterFunction("print", typeof(Program).GetMethod(nameof(LuaPrint),
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!);

            // Initialize _G.LastError
            lua["_G.LastError"] = "";

            // Execute the script
            var results = lua.DoFile(scriptPath);

            // Parse return value
            if (results != null && results.Length > 0)
            {
                var ret = results[0];
                if (ret is long l) return (int)l;
                if (ret is double d) return (int)d;
                if (ret is int i) return i;

                // Try parsing string
                if (ret is string s && int.TryParse(s, out int parsed))
                    return parsed;
            }

            Console.Error.WriteLine($"[Script] Warning: {Path.GetFileName(scriptPath)} did not return a valid integer code, defaulting to 2.");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Script] Exception executing {Path.GetFileName(scriptPath)}: {ex.GetBaseException().Message}");
            return 2;
        }
        finally
        {
            lua?.Dispose();
        }
    }

    /// <summary>
    /// Lua print function redirect — prints all args to Console.
    /// </summary>
    private static void LuaPrint(params object[] args)
    {
        var parts = new List<string>();
        foreach (var arg in args)
        {
            parts.Add(arg?.ToString() ?? "nil");
        }
        Console.WriteLine($"[Lua] {string.Join("\t", parts)}");
    }
}
