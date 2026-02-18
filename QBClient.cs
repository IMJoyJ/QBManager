using System.Net;
using System.Text.Json;
using System.Web;
using NLua;

namespace QBManager;

/// <summary>
/// qBittorrent WebUI API client, injected into Lua as "qb".
/// All public methods follow the error protocol:
///   Success → return valid value (never null)
///   Failure → set _G.LastError, return null (nil in Lua)
/// </summary>
public class QBClient
{
    private readonly HttpClient _http;
    private readonly ServerConfig _server;
    private readonly CookieContainer _cookies;

    private NLua.Lua? _lua;

    public QBClient(HttpClient httpClient, ServerConfig server, CookieContainer cookies)
    {
        _http = httpClient;
        _server = server;
        _cookies = cookies;
    }

    /// <summary>
    /// Bind the current Lua state so SetLastError can write to _G.LastError.
    /// Must be called before each script execution.
    /// </summary>
    public void BindLua(NLua.Lua lua)
    {
        _lua = lua;
    }

    // ─────────────────────────────────────────────
    //  Helper: set _G.LastError in Lua
    // ─────────────────────────────────────────────

    private void SetLastError(string message)
    {
        if (_lua != null)
        {
            _lua["_G.LastError"] = message;
        }
        Console.Error.WriteLine($"[QBClient Error] {message}");
    }

    // ─────────────────────────────────────────────
    //  Authentication
    // ─────────────────────────────────────────────

    public async Task<bool> LoginAsync()
    {
        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = _server.Username,
            ["password"] = _server.Password
        });

        var request = new HttpRequestMessage(HttpMethod.Post, $"{_server.BaseUrl}/api/v2/auth/login")
        {
            Content = content
        };
        request.Headers.Add("Referer", _server.BaseUrl);

        var response = await _http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            Console.Error.WriteLine("[QBClient] Login failed: IP is banned for too many failed attempts.");
            return false;
        }

        if (body.Contains("Ok.", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("[QBClient] Login successful.");
            return true;
        }

        Console.Error.WriteLine($"[QBClient] Login failed: {body}");
        return false;
    }

    // ─────────────────────────────────────────────
    //  Internal HTTP helpers with auto-relogin on 403
    // ─────────────────────────────────────────────

    private async Task<HttpResponseMessage> GetAsync(string url)
    {
        var response = await _http.GetAsync(url);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            Console.WriteLine("[QBClient] Got 403, attempting re-login...");
            if (await LoginAsync())
            {
                response = await _http.GetAsync(url);
            }
        }
        return response;
    }

    private async Task<HttpResponseMessage> PostFormAsync(string url, Dictionary<string, string> form)
    {
        var content = new FormUrlEncodedContent(form);
        var response = await _http.PostAsync(url, content);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            Console.WriteLine("[QBClient] Got 403, attempting re-login...");
            if (await LoginAsync())
            {
                content = new FormUrlEncodedContent(form);
                response = await _http.PostAsync(url, content);
            }
        }
        return response;
    }

    private async Task<HttpResponseMessage> PostMultipartAsync(string url, MultipartFormDataContent content)
    {
        var response = await _http.PostAsync(url, content);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            Console.WriteLine("[QBClient] Got 403, attempting re-login...");
            if (await LoginAsync())
            {
                response = await _http.PostAsync(url, content);
            }
        }
        return response;
    }

    // ─────────────────────────────────────────────
    //  JSON → Lua Table conversion helpers
    // ─────────────────────────────────────────────

    /// <summary>
    /// Convert a JsonElement to a Lua-compatible object.
    /// Arrays → object[] (1-indexed LuaTable created via DoString)
    /// Objects → Dictionary&lt;object,object&gt;
    /// </summary>
    private object? JsonElementToLuaObject(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString() ?? "";
            case JsonValueKind.Number:
                if (element.TryGetInt64(out long l)) return l;
                return element.GetDouble();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
            case JsonValueKind.Array:
                return JsonArrayToLuaTable(element);
            case JsonValueKind.Object:
                return JsonObjectToLuaTable(element);
            default:
                return element.ToString();
        }
    }

    /// <summary>
    /// Convert a JSON array to a Lua table (via NLua).
    /// Creates a new table in Lua and populates it with 1-based integer keys.
    /// </summary>
    private LuaTable JsonArrayToLuaTable(JsonElement array)
    {
        if (_lua == null) throw new InvalidOperationException("Lua state not bound");

        var table = (LuaTable)_lua.DoString("return {}")[0];
        int index = 1;
        foreach (var item in array.EnumerateArray())
        {
            var value = JsonElementToLuaObject(item);
            if (value is LuaTable)
            {
                // For nested tables, we need to use Lua to assign them
                _lua["__temp_val"] = value;
                _lua["__temp_tbl"] = table;
                _lua.DoString($"__temp_tbl[{index}] = __temp_val");
                _lua["__temp_val"] = null;
                _lua["__temp_tbl"] = null;
            }
            else
            {
                table[index] = value;
            }
            index++;
        }
        return table;
    }

    /// <summary>
    /// Convert a JSON object to a Lua table (via NLua).
    /// </summary>
    private LuaTable JsonObjectToLuaTable(JsonElement obj)
    {
        if (_lua == null) throw new InvalidOperationException("Lua state not bound");

        var table = (LuaTable)_lua.DoString("return {}")[0];
        foreach (var prop in obj.EnumerateObject())
        {
            var value = JsonElementToLuaObject(prop.Value);
            if (value is LuaTable)
            {
                _lua["__temp_val"] = value;
                _lua["__temp_tbl"] = table;
                _lua.DoString($"__temp_tbl[\"{EscapeLuaString(prop.Name)}\"] = __temp_val");
                _lua["__temp_val"] = null;
                _lua["__temp_tbl"] = null;
            }
            else
            {
                table[prop.Name] = value;
            }
        }
        return table;
    }

    private static string EscapeLuaString(string s)
    {
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
    }

    // ─────────────────────────────────────────────
    //  WebUI API: Torrent Operations
    // ─────────────────────────────────────────────

    /// <summary>
    /// Get all torrents. Returns a Lua table array.
    /// Each element is a table with: hash, name, content_path, category, state, size, save_path, progress, etc.
    /// </summary>
    public object? GetTorrents()
    {
        return GetTorrentsInternal(null);
    }

    /// <summary>
    /// Get specific torrents by hash (pipe-separated). Returns a Lua table array.
    /// </summary>
    public object? GetTorrents(string hashes)
    {
        return GetTorrentsInternal(hashes);
    }

    private object? GetTorrentsInternal(string? hashes)
    {
        try
        {
            var url = $"{_server.BaseUrl}/api/v2/torrents/info";
            if (!string.IsNullOrEmpty(hashes))
            {
                url += $"?hashes={hashes}";
            }

            var response = GetAsync(url).Result;
            response.EnsureSuccessStatusCode();
            var json = response.Content.ReadAsStringAsync().Result;
            var doc = JsonDocument.Parse(json);
            return JsonArrayToLuaTable(doc.RootElement);
        }
        catch (Exception ex)
        {
            SetLastError($"GetTorrents failed: {ex.GetBaseException().Message}");
            return null;
        }
    }

    /// <summary>
    /// Get files for a specific torrent. Returns a Lua table array.
    /// </summary>
    public object? GetFiles(string hash)
    {
        try
        {
            var response = GetAsync($"{_server.BaseUrl}/api/v2/torrents/files?hash={hash}").Result;
            response.EnsureSuccessStatusCode();
            var json = response.Content.ReadAsStringAsync().Result;
            var doc = JsonDocument.Parse(json);
            return JsonArrayToLuaTable(doc.RootElement);
        }
        catch (Exception ex)
        {
            SetLastError($"GetFiles failed: {ex.GetBaseException().Message}");
            return null;
        }
    }

    /// <summary>
    /// Delete a torrent. deleteFiles: true to also remove downloaded data.
    /// </summary>
    public object? DeleteTorrent(string hash, bool deleteFiles)
    {
        try
        {
            var form = new Dictionary<string, string>
            {
                ["hashes"] = hash,
                ["deleteFiles"] = deleteFiles.ToString().ToLower()
            };
            var response = PostFormAsync($"{_server.BaseUrl}/api/v2/torrents/delete", form).Result;
            response.EnsureSuccessStatusCode();
            return true;
        }
        catch (Exception ex)
        {
            SetLastError($"DeleteTorrent failed: {ex.GetBaseException().Message}");
            return null;
        }
    }

    /// <summary>
    /// Add a torrent by URL.
    /// </summary>
    public object? AddTorrent(string category, string savePath, bool skipHashCheck, string torrentUrl)
    {
        try
        {
            var content = new MultipartFormDataContent();
            content.Add(new StringContent(torrentUrl), "urls");
            content.Add(new StringContent(savePath), "savepath");
            content.Add(new StringContent(category), "category");
            content.Add(new StringContent(skipHashCheck ? "true" : "false"), "skip_checking");

            var response = PostMultipartAsync($"{_server.BaseUrl}/api/v2/torrents/add", content).Result;
            response.EnsureSuccessStatusCode();
            return true;
        }
        catch (Exception ex)
        {
            SetLastError($"AddTorrent failed: {ex.GetBaseException().Message}");
            return null;
        }
    }

    /// <summary>
    /// Set the download location for a torrent.
    /// </summary>
    public object? SetLocation(string hash, string newPath)
    {
        try
        {
            var form = new Dictionary<string, string>
            {
                ["hashes"] = hash,
                ["location"] = newPath
            };
            var response = PostFormAsync($"{_server.BaseUrl}/api/v2/torrents/setLocation", form).Result;
            response.EnsureSuccessStatusCode();
            return true;
        }
        catch (Exception ex)
        {
            SetLastError($"SetLocation failed: {ex.GetBaseException().Message}");
            return null;
        }
    }

    /// <summary>
    /// Change the category of a torrent.
    /// </summary>
    public object? ChangeCategory(string hash, string category)
    {
        try
        {
            var form = new Dictionary<string, string>
            {
                ["hashes"] = hash,
                ["category"] = category
            };
            var response = PostFormAsync($"{_server.BaseUrl}/api/v2/torrents/setCategory", form).Result;
            response.EnsureSuccessStatusCode();
            return true;
        }
        catch (Exception ex)
        {
            SetLastError($"ChangeCategory failed: {ex.GetBaseException().Message}");
            return null;
        }
    }



    /// <summary>
    /// Get trackers for a specific torrent. Returns a Lua table array.
    /// </summary>
    public object? GetTrackers(string hash)
    {
        try
        {
            var response = GetAsync($"{_server.BaseUrl}/api/v2/torrents/trackers?hash={hash}").Result;
            response.EnsureSuccessStatusCode();
            var json = response.Content.ReadAsStringAsync().Result;
            var doc = JsonDocument.Parse(json);
            return JsonArrayToLuaTable(doc.RootElement);
        }
        catch (Exception ex)
        {
            SetLastError($"GetTrackers failed: {ex.GetBaseException().Message}");
            return null;
        }
    }

    /// <summary>
    /// Check if any tracker of a torrent matches the given pattern (substring).
    /// </summary>
    public object? HasTracker(string hash, string pattern)
    {
        try
        {
            var response = GetAsync($"{_server.BaseUrl}/api/v2/torrents/trackers?hash={hash}").Result;
            response.EnsureSuccessStatusCode();
            var json = response.Content.ReadAsStringAsync().Result;
            var doc = JsonDocument.Parse(json);

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("url", out var urlProp) && urlProp.ValueKind == JsonValueKind.String)
                {
                    var url = urlProp.GetString() ?? "";
                    if (url.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            return false;
        }
        catch (Exception ex)
        {
            SetLastError($"HasTracker failed: {ex.GetBaseException().Message}");
            return null;
        }
    }

    /// <summary>
    /// Rename a file inside a torrent.
    /// </summary>
    public object? RenameFile(string hash, string oldPath, string newPath)
    {
        try
        {
            var form = new Dictionary<string, string>
            {
                ["hash"] = hash,
                ["oldPath"] = oldPath,
                ["newPath"] = newPath
            };
            var response = PostFormAsync($"{_server.BaseUrl}/api/v2/torrents/renameFile", form).Result;
            response.EnsureSuccessStatusCode();
            return true;
        }
        catch (Exception ex)
        {
            SetLastError($"RenameFile failed: {ex.GetBaseException().Message}");
            return null;
        }
    }

    /// <summary>
    /// Rename a folder inside a torrent.
    /// </summary>
    public object? RenameFolder(string hash, string oldPath, string newPath)
    {
        try
        {
            var form = new Dictionary<string, string>
            {
                ["hash"] = hash,
                ["oldPath"] = oldPath,
                ["newPath"] = newPath
            };
            var response = PostFormAsync($"{_server.BaseUrl}/api/v2/torrents/renameFolder", form).Result;
            response.EnsureSuccessStatusCode();
            return true;
        }
        catch (Exception ex)
        {
            SetLastError($"RenameFolder failed: {ex.GetBaseException().Message}");
            return null;
        }
    }

    // ─────────────────────────────────────────────
    //  File System Operations (Local)
    // ─────────────────────────────────────────────

    /// <summary>
    /// Export .torrent file from qBittorrent (hidden API) and save to local path.
    /// Uses GET /api/v2/torrents/export?hash={hash}
    /// </summary>
    public object? ExportTorrentFile(string hash, string destinationPath, bool overwrite = true)
    {
        try
        {
            if (!overwrite && File.Exists(destinationPath))
            {
                SetLastError($"ExportTorrentFile: Destination already exists and overwrite is false: {destinationPath}");
                return null;
            }

            var response = GetAsync($"{_server.BaseUrl}/api/v2/torrents/export?hash={hash}").Result;
            response.EnsureSuccessStatusCode();

            var bytes = response.Content.ReadAsByteArrayAsync().Result;
            if (bytes.Length == 0)
            {
                SetLastError($"ExportTorrentFile: Received empty response for hash {hash}");
                return null;
            }

            var dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllBytes(destinationPath, bytes);
            return true;
        }
        catch (Exception ex)
        {
            SetLastError($"ExportTorrentFile failed: {ex.GetBaseException().Message}");
            return null;
        }
    }

    /// <summary>
    /// Copy torrent content files to destination using System.IO.
    /// Tracks copied files for rollback on failure.
    /// </summary>
    public object? CopyTorrentFiles(string hash, string destinationPath, bool overwrite = false)
    {
        var copiedFiles = new List<string>();
        var createdDirs = new List<string>();

        try
        {
            // Step 1: Get the torrent's content_path from the API
            var response = GetAsync($"{_server.BaseUrl}/api/v2/torrents/info?hashes={hash}").Result;
            response.EnsureSuccessStatusCode();
            var json = response.Content.ReadAsStringAsync().Result;
            var doc = JsonDocument.Parse(json);

            var torrents = doc.RootElement;
            if (torrents.GetArrayLength() == 0)
            {
                SetLastError($"CopyTorrentFiles: Torrent not found for hash {hash}");
                return null;
            }

            var contentPath = torrents[0].GetProperty("content_path").GetString();
            if (string.IsNullOrEmpty(contentPath))
            {
                SetLastError($"CopyTorrentFiles: content_path is empty for hash {hash}");
                return null;
            }

            // Step 2: Determine if it's a file or directory and copy
            if (File.Exists(contentPath))
            {
                // Single file torrent
                var destFile = Path.Combine(destinationPath, Path.GetFileName(contentPath));
                var destDir = Path.GetDirectoryName(destFile);
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                {
                    Directory.CreateDirectory(destDir);
                    createdDirs.Add(destDir);
                }

                if (!overwrite && File.Exists(destFile))
                {
                    SetLastError($"CopyTorrentFiles: Destination file already exists: {destFile}");
                    return null;
                }

                File.Copy(contentPath, destFile, overwrite);
                copiedFiles.Add(destFile);
            }
            else if (Directory.Exists(contentPath))
            {
                // Multi-file torrent (directory)
                CopyDirectoryRecursive(contentPath, destinationPath, overwrite, copiedFiles, createdDirs);
            }
            else
            {
                SetLastError($"CopyTorrentFiles: Source path does not exist: {contentPath}");
                return null;
            }

            return true;
        }
        catch (Exception ex)
        {
            // Rollback: delete only the files we copied
            Console.Error.WriteLine($"[QBClient] CopyTorrentFiles failed, rolling back {copiedFiles.Count} copied files...");

            foreach (var file in copiedFiles)
            {
                try
                {
                    if (File.Exists(file))
                    {
                        File.Delete(file);
                    }
                }
                catch (Exception rollbackEx)
                {
                    Console.Error.WriteLine($"[QBClient] Rollback: failed to delete {file}: {rollbackEx.Message}");
                }
            }

            // Remove created directories (in reverse order, deepest first) if empty
            createdDirs.Sort((a, b) => b.Length.CompareTo(a.Length));
            foreach (var dir in createdDirs)
            {
                try
                {
                    if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    {
                        Directory.Delete(dir);
                    }
                }
                catch (Exception rollbackEx)
                {
                    Console.Error.WriteLine($"[QBClient] Rollback: failed to delete dir {dir}: {rollbackEx.Message}");
                }
            }

            SetLastError($"CopyTorrentFiles failed (rolled back): {ex.GetBaseException().Message}");
            return null;
        }
    }

    private static void CopyDirectoryRecursive(
        string sourceDir, string destDir, bool overwrite,
        List<string> copiedFiles, List<string> createdDirs)
    {
        // Ensure the folder name itself is preserved in destination
        var dirName = Path.GetFileName(sourceDir);
        var targetDir = Path.Combine(destDir, dirName);

        if (!Directory.Exists(targetDir))
        {
            Directory.CreateDirectory(targetDir);
            createdDirs.Add(targetDir);
        }

        // Copy files
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(targetDir, Path.GetFileName(file));
            if (!overwrite && File.Exists(destFile))
            {
                throw new IOException($"Destination file already exists: {destFile}");
            }
            File.Copy(file, destFile, overwrite);
            copiedFiles.Add(destFile);
        }

        // Recurse into subdirectories
        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            CopyDirectoryRecursive(subDir, targetDir, overwrite, copiedFiles, createdDirs);
        }
    }

    /// <summary>
    /// Get available free space on the drive containing the given path.
    /// Returns bytes as long.
    /// </summary>
    public object? GetLocalFreeSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root))
            {
                SetLastError($"GetLocalFreeSpace: Cannot determine drive root for path: {path}");
                return null;
            }

            var driveInfo = new DriveInfo(root);
            return driveInfo.AvailableFreeSpace;
        }
        catch (Exception ex)
        {
            SetLastError($"GetLocalFreeSpace failed: {ex.GetBaseException().Message}");
            return null;
        }
    }

    /// <summary>
    /// Check if a local path (file or directory) exists.
    /// </summary>
    public object? PathExists(string path)
    {
        try
        {
            return File.Exists(path) || Directory.Exists(path);
        }
        catch (Exception ex)
        {
            SetLastError($"PathExists failed: {ex.GetBaseException().Message}");
            return null;
        }
    }

    // ─────────────────────────────────────────────
    //  Utility
    // ─────────────────────────────────────────────

    /// <summary>
    /// URL-encode a string.
    /// </summary>
    public object? UrlEncode(string str)
    {
        try
        {
            return HttpUtility.UrlEncode(str);
        }
        catch (Exception ex)
        {
            SetLastError($"UrlEncode failed: {ex.GetBaseException().Message}");
            return null;
        }
    }

    /// <summary>
    /// URL-decode a string.
    /// </summary>
    public object? UrlDecode(string str)
    {
        try
        {
            return HttpUtility.UrlDecode(str);
        }
        catch (Exception ex)
        {
            SetLastError($"UrlDecode failed: {ex.GetBaseException().Message}");
            return null;
        }
    }

    /// <summary>
    /// Sleep for N seconds.
    /// </summary>
    public object? Sleep(int seconds)
    {
        try
        {
            if (seconds > 0)
                Thread.Sleep(seconds * 1000);
            return true;
        }
        catch (Exception ex)
        {
            SetLastError($"Sleep failed: {ex.GetBaseException().Message}");
            return null;
        }
    }

    /// <summary>
    /// Check if path is a directory.
    /// </summary>
    public object? IsDirectory(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception ex)
        {
            SetLastError($"IsDirectory failed: {ex.GetBaseException().Message}");
            return null;
        }
    }
}
