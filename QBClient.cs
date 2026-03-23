using System.Net;
using System.Text.Json;
using System.Web;
using Microsoft.Data.Sqlite;
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
    private string? _dbPath;

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
    /// Pause (stop) torrent(s).
    /// </summary>
    public object? PauseTorrents(string hashes)
    {
        try
        {
            var form = new Dictionary<string, string>
            {
                ["hashes"] = hashes
            };
            var response = PostFormAsync($"{_server.BaseUrl}/api/v2/torrents/stop", form).Result;
            response.EnsureSuccessStatusCode();
            return true;
        }
        catch (Exception ex)
        {
            SetLastError($"PauseTorrents failed: {ex.GetBaseException().Message}");
            return null;
        }
    }

    /// <summary>
    /// Resume (start) torrent(s).
    /// </summary>
    public object? ResumeTorrents(string hashes)
    {
        return ResumeTorrents(hashes, false);
    }

    /// <summary>
    /// Resume (start) torrent(s) with an option to force start.
    /// </summary>
    public object? ResumeTorrents(string hashes, bool forceStart)
    {
        try
        {
            var form = new Dictionary<string, string>
            {
                ["hashes"] = hashes
            };
            var response = PostFormAsync($"{_server.BaseUrl}/api/v2/torrents/start", form).Result;
            response.EnsureSuccessStatusCode();

            var forceForm = new Dictionary<string, string>
            {
                ["hashes"] = hashes,
                ["value"] = forceStart ? "true" : "false"
            };
            var forceResponse = PostFormAsync($"{_server.BaseUrl}/api/v2/torrents/setForceStart", forceForm).Result;
            forceResponse.EnsureSuccessStatusCode();

            return true;
        }
        catch (Exception ex)
        {
            SetLastError($"ResumeTorrents failed: {ex.GetBaseException().Message}");
            return null;
        }
    }

    /// <summary>
    /// Set the force start state for one or more torrents.
    /// </summary>
    public object? SetForceStart(string hashes, bool value)
    {
        try
        {
            var form = new Dictionary<string, string>
            {
                ["hashes"] = hashes,
                ["value"] = value ? "true" : "false"
            };
            var response = PostFormAsync($"{_server.BaseUrl}/api/v2/torrents/setForceStart", form).Result;
            response.EnsureSuccessStatusCode();
            return true;
        }
        catch (Exception ex)
        {
            SetLastError($"SetForceStart failed: {ex.GetBaseException().Message}");
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
    /// Add a torrent from a local .torrent file.
    /// </summary>
    public object? AddTorrentFile(string category, string savePath, bool skipHashCheck, string torrentFilePath)
    {
        try
        {
            if (!File.Exists(torrentFilePath))
            {
                SetLastError($"AddTorrentFile: File not found: {torrentFilePath}");
                return null;
            }

            var content = new MultipartFormDataContent();
            var fileBytes = File.ReadAllBytes(torrentFilePath);
            var fileContent = new ByteArrayContent(fileBytes);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-bittorrent");
            content.Add(fileContent, "torrents", Path.GetFileName(torrentFilePath));
            content.Add(new StringContent(savePath), "savepath");
            content.Add(new StringContent(category), "category");
            content.Add(new StringContent(skipHashCheck ? "true" : "false"), "skip_checking");

            var response = PostMultipartAsync($"{_server.BaseUrl}/api/v2/torrents/add", content).Result;
            response.EnsureSuccessStatusCode();
            return true;
        }
        catch (Exception ex)
        {
            SetLastError($"AddTorrentFile failed: {ex.GetBaseException().Message}");
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
    /// Add trackers to a torrent.
    /// </summary>
    public object? AddTrackers(string hash, string urls)
    {
        try
        {
            var form = new Dictionary<string, string>
            {
                ["hash"] = hash,
                ["urls"] = urls
            };
            var response = PostFormAsync($"{_server.BaseUrl}/api/v2/torrents/addTrackers", form).Result;
            response.EnsureSuccessStatusCode();
            return true;
        }
        catch (Exception ex)
        {
            SetLastError($"AddTrackers failed: {ex.GetBaseException().Message}");
            return null;
        }
    }

    /// <summary>
    /// Remove trackers from a torrent.
    /// </summary>
    public object? RemoveTrackers(string hash, string urls)
    {
        try
        {
            var form = new Dictionary<string, string>
            {
                ["hash"] = hash,
                ["urls"] = urls
            };
            var response = PostFormAsync($"{_server.BaseUrl}/api/v2/torrents/removeTrackers", form).Result;
            response.EnsureSuccessStatusCode();
            return true;
        }
        catch (Exception ex)
        {
            SetLastError($"RemoveTrackers failed: {ex.GetBaseException().Message}");
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
    /// overwrite: true = overwrite existing, false = skip existing, null = error if exists
    /// </summary>
    public object? CopyTorrentFiles(string hash, string destinationPath, bool? overwrite = null)
    {
        var copiedFiles = new List<string>();
        var createdDirs = new List<string>();
        int skippedCount = 0;

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

            // Safety: refuse to copy if content_path == save_path
            // This happens when a torrent has no single root folder (e.g. after a crashed rename),
            // and would cause us to recursively copy the ENTIRE download directory.
            var savePath = torrents[0].GetProperty("save_path").GetString();
            if (!string.IsNullOrEmpty(savePath))
            {
                var normalizedContent = Path.GetFullPath(contentPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var normalizedSave = Path.GetFullPath(savePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(normalizedContent, normalizedSave, StringComparison.OrdinalIgnoreCase))
                {
                    SetLastError($"CopyTorrentFiles: content_path equals save_path ({contentPath}), refusing to copy entire directory. Torrent may have inconsistent folder structure.");
                    return null;
                }
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

                if (File.Exists(destFile))
                {
                    if (overwrite == null)
                    {
                        // null = error
                        SetLastError($"CopyTorrentFiles: Destination file already exists: {destFile}");
                        return null;
                    }
                    else if (overwrite == false)
                    {
                        // false = skip
                        skippedCount++;
                    }
                    else
                    {
                        // true = overwrite
                        File.Copy(contentPath, destFile, true);
                        copiedFiles.Add(destFile);
                    }
                }
                else
                {
                    File.Copy(contentPath, destFile, false);
                    copiedFiles.Add(destFile);
                }
            }
            else if (Directory.Exists(contentPath))
            {
                // Multi-file torrent (directory)
                CopyDirectoryRecursive(contentPath, destinationPath, overwrite, copiedFiles, createdDirs, ref skippedCount);
            }
            else
            {
                SetLastError($"CopyTorrentFiles: Source path does not exist: {contentPath}");
                return null;
            }

            if (skippedCount > 0)
            {
                Console.Error.WriteLine($"[QBClient] CopyTorrentFiles: skipped {skippedCount} existing files");
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
        string sourceDir, string destDir, bool? overwrite,
        List<string> copiedFiles, List<string> createdDirs, ref int skippedCount)
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
            if (File.Exists(destFile))
            {
                if (overwrite == null)
                {
                    // null = error
                    throw new IOException($"Destination file already exists: {destFile}");
                }
                else if (overwrite == false)
                {
                    // false = skip
                    skippedCount++;
                    continue;
                }
                // true = overwrite, fall through to File.Copy
            }
            File.Copy(file, destFile, overwrite == true);
            copiedFiles.Add(destFile);
        }

        // Recurse into subdirectories
        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            CopyDirectoryRecursive(subDir, targetDir, overwrite, copiedFiles, createdDirs, ref skippedCount);
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

    /// <summary>
    /// Prompt the user with a yes/no question via console.
    /// Returns true for yes, false for no.
    /// </summary>
    public bool AskYesNo(string prompt)
    {
        Console.Error.Write(prompt + " [y/N]: ");
        Console.Error.Flush();
        var input = Console.ReadLine();
        return input != null &&
               (input.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) ||
                input.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Move (rename) a local file. Works across directories on the same drive.
    /// </summary>
    public object? MoveLocalFile(string sourcePath, string destinationPath)
    {
        try
        {
            var destDir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
            {
                Directory.CreateDirectory(destDir);
            }
            File.Move(sourcePath, destinationPath);
            return true;
        }
        catch (Exception ex)
        {
            SetLastError($"MoveLocalFile failed: {ex.GetBaseException().Message}");
            return null;
        }
    }

    /// <summary>
    /// Move (rename) a local directory. Fails if destination already exists.
    /// </summary>
    public object? MoveLocalDirectory(string sourcePath, string destinationPath)
    {
        try
        {
            Directory.Move(sourcePath, destinationPath);
            return true;
        }
        catch (Exception ex)
        {
            SetLastError($"MoveLocalDirectory failed: {ex.GetBaseException().Message}");
            return null;
        }
    }

    /// <summary>
    /// Merge contents of sourceDir into destDir (move all files/subdirs).
    /// Creates destDir if it doesn't exist. Removes sourceDir after merge.
    /// Returns the number of files moved, or nil on error.
    /// </summary>
    public object? MergeLocalDirectory(string sourceDir, string destDir)
    {
        try
        {
            if (!Directory.Exists(sourceDir))
            {
                SetLastError($"MergeLocalDirectory: Source does not exist: {sourceDir}");
                return null;
            }

            if (!Directory.Exists(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            int movedCount = MergeDirectoryRecursive(sourceDir, destDir);

            // Try to remove source directory (should be empty after merge)
            try
            {
                if (Directory.Exists(sourceDir))
                {
                    Directory.Delete(sourceDir, false);
                }
            }
            catch
            {
                Console.Error.WriteLine($"[QBClient] MergeLocalDirectory: Could not remove source dir (not empty?): {sourceDir}");
            }

            return (long)movedCount;
        }
        catch (Exception ex)
        {
            SetLastError($"MergeLocalDirectory failed: {ex.GetBaseException().Message}");
            return null;
        }
    }

    private static int MergeDirectoryRecursive(string sourceDir, string destDir)
    {
        int count = 0;

        // Move files
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(destDir, Path.GetFileName(file));
            if (File.Exists(destFile))
            {
                // Skip if destination already exists (same file, different root)
                Console.Error.WriteLine($"[QBClient] MergeLocalDirectory: Skipping existing file: {destFile}");
                continue;
            }
            File.Move(file, destFile);
            count++;
        }

        // Recurse into subdirectories
        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            var destSubDir = Path.Combine(destDir, Path.GetFileName(subDir));
            if (!Directory.Exists(destSubDir))
            {
                // Simple move if destination subdir doesn't exist
                Directory.Move(subDir, destSubDir);
                count += Directory.GetFiles(destSubDir, "*", SearchOption.AllDirectories).Length;
            }
            else
            {
                // Merge recursively if both exist
                count += MergeDirectoryRecursive(subDir, destSubDir);
                try { Directory.Delete(subDir, false); } catch { }
            }
        }

        return count;
    }


    // ─────────────────────────────────────────────
    //  SQLite Database
    // ─────────────────────────────────────────────

    /// <summary>
    /// Set the SQLite database file path. Called by Program.cs at startup.
    /// </summary>
    public void SetDatabasePath(string path)
    {
        _dbPath = path;
    }

    /// <summary>
    /// Helper: create and bind parameters @p1, @p2, ... from args.
    /// </summary>
    private void BindDbParameters(SqliteCommand cmd, object[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            var val = args[i];
            cmd.Parameters.AddWithValue($"@p{i + 1}", val ?? DBNull.Value);
        }
    }

    /// <summary>
    /// Execute a non-query SQL statement (INSERT/UPDATE/DELETE/CREATE).
    /// Returns the number of affected rows.
    /// </summary>
    public object? DbExecute(string sql, params object[] args)
    {
        try
        {
            if (string.IsNullOrEmpty(_dbPath))
            {
                SetLastError("DbExecute: database_path not configured.");
                return null;
            }

            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            BindDbParameters(cmd, args);
            var affected = cmd.ExecuteNonQuery();
            return (long)affected;
        }
        catch (Exception ex)
        {
            SetLastError($"DbExecute failed: {ex.GetBaseException().Message}");
            return null;
        }
    }

    /// <summary>
    /// Execute a SELECT query. Returns a Lua table array of row-dictionaries.
    /// Each row is a table { column_name = value, ... }.
    /// </summary>
    public object? DbQuery(string sql, params object[] args)
    {
        try
        {
            if (string.IsNullOrEmpty(_dbPath))
            {
                SetLastError("DbQuery: database_path not configured.");
                return null;
            }
            if (_lua == null)
            {
                SetLastError("DbQuery: Lua state not bound.");
                return null;
            }

            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            BindDbParameters(cmd, args);
            using var reader = cmd.ExecuteReader();

            // Create result table
            var resultTable = (LuaTable)_lua.DoString("return {}")[0];
            int rowIndex = 0;

            while (reader.Read())
            {
                rowIndex++;
                var rowTable = (LuaTable)_lua.DoString("return {}")[0];

                for (int col = 0; col < reader.FieldCount; col++)
                {
                    var colName = reader.GetName(col);
                    if (reader.IsDBNull(col))
                    {
                        // nil — just skip, Lua tables have nil for missing keys
                        continue;
                    }

                    var raw = reader.GetValue(col);
                    // SQLite types: INTEGER -> long, REAL -> double, TEXT -> string, BLOB -> byte[]
                    object value = raw switch
                    {
                        long l => l,
                        double d => d,
                        string s => s,
                        byte[] b => System.Text.Encoding.UTF8.GetString(b),
                        _ => raw.ToString()!
                    };

                    // Use same pattern as JsonObjectToLuaTable
                    rowTable[colName] = value;
                }

                // Insert row into result array (1-based index)
                _lua["__temp_val"] = rowTable;
                _lua["__temp_tbl"] = resultTable;
                _lua.DoString($"__temp_tbl[{rowIndex}] = __temp_val");
                _lua["__temp_val"] = null;
                _lua["__temp_tbl"] = null;
            }

            return resultTable;
        }
        catch (Exception ex)
        {
            SetLastError($"DbQuery failed: {ex.GetBaseException().Message}");
            return null;
        }
    }

    /// <summary>
    /// Execute a scalar query. Returns the first column of the first row.
    /// </summary>
    public object? DbScalar(string sql, params object[] args)
    {
        try
        {
            if (string.IsNullOrEmpty(_dbPath))
            {
                SetLastError("DbScalar: database_path not configured.");
                return null;
            }

            using var conn = new SqliteConnection($"Data Source={_dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            BindDbParameters(cmd, args);
            var result = cmd.ExecuteScalar();

            if (result == null || result == DBNull.Value)
                return null;

            return result switch
            {
                long l => l,
                double d => d,
                string s => s,
                byte[] b => System.Text.Encoding.UTF8.GetString(b),
                _ => result.ToString()!
            };
        }
        catch (Exception ex)
        {
            SetLastError($"DbScalar failed: {ex.GetBaseException().Message}");
            return null;
        }
    }

    // ─────────────────────────────────────────────
    //  File-Level Operations
    // ─────────────────────────────────────────────

    /// <summary>
    /// Get the list of files belonging to a torrent.
    /// Returns a Lua table array of { name, size, progress, priority, ... }.
    /// The "name" field is the path relative to save_path.
    /// </summary>
    public object? GetTorrentFiles(string hash)
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
            SetLastError($"GetTorrentFiles failed: {ex.GetBaseException().Message}");
            return null;
        }
    }

    /// <summary>
    /// Delete a single local file. Returns true on success, nil on failure.
    /// </summary>
    public object? DeleteLocalFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                // File doesn't exist — treat as success (already gone)
                return true;
            }
            File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            SetLastError($"DeleteLocalFile failed: {ex.GetBaseException().Message}");
            return null;
        }
    }

    /// <summary>
    /// Delete a local directory ONLY if it is empty. Returns true if deleted,
    /// false if not empty, nil on error.
    /// </summary>
    public object? DeleteLocalDir(string path)
    {
        try
        {
            if (!Directory.Exists(path))
                return true; // already gone

            if (Directory.EnumerateFileSystemEntries(path).Any())
                return false; // not empty

            Directory.Delete(path, false);
            return true;
        }
        catch (Exception ex)
        {
            SetLastError($"DeleteLocalDir failed: {ex.GetBaseException().Message}");
            return null;
        }
    }

    // ─────────────────────────────────────────────
    //  RSS Feed
    // ─────────────────────────────────────────────

    /// <summary>
    /// Fetch and parse an RSS 2.0 feed. Returns a Lua table array where each element
    /// is a table representing one &lt;item&gt; with the following fields:
    ///   title        string  — item title (CDATA stripped)
    ///   link         string  — detail page URL
    ///   hash         string  — torrent info-hash from &lt;guid isPermaLink="false"&gt;
    ///   enclosure_url    string  — .torrent download URL (decoded from XML entities)
    ///   enclosure_length number  — torrent size in bytes
    ///   pub_date     string  — raw RFC-822 publish date string
    ///   pub_time     number  — publish date as Unix timestamp (or 0 if unparseable)
    ///   category     string  — category text
    ///   author       string  — author field
    /// </summary>
    public object? GetRSS(string url)
    {
        if (_lua == null)
        {
            SetLastError("GetRSS: Lua state not bound.");
            return null;
        }

        try
        {
            var response = _http.GetAsync(url).Result;
            response.EnsureSuccessStatusCode();
            var xml = response.Content.ReadAsStringAsync().Result;

            var doc = System.Xml.Linq.XDocument.Parse(xml);
            var channel = doc.Root?.Element("channel");
            if (channel == null)
            {
                SetLastError("GetRSS: No <channel> element found in RSS response.");
                return null;
            }

            var resultTable = (LuaTable)_lua.DoString("return {}")[0];
            int rowIndex = 0;

            foreach (var item in channel.Elements("item"))
            {
                rowIndex++;
                var rowTable = (LuaTable)_lua.DoString("return {}")[0];

                // title
                rowTable["title"] = item.Element("title")?.Value ?? "";

                // link
                rowTable["link"] = item.Element("link")?.Value ?? "";

                // guid → hash (isPermaLink="false" means it's the info-hash)
                rowTable["hash"] = item.Element("guid")?.Value ?? "";

                // enclosure: <enclosure url="..." length="..." type="..."/>
                var enclosure = item.Element("enclosure");
                rowTable["enclosure_url"]    = enclosure?.Attribute("url")?.Value ?? "";
                var lenStr = enclosure?.Attribute("length")?.Value ?? "0";
                rowTable["enclosure_length"] = long.TryParse(lenStr, out var lenVal) ? lenVal : 0L;

                // pubDate
                var pubDateStr = item.Element("pubDate")?.Value ?? "";
                rowTable["pub_date"] = pubDateStr;
                long pubTime = 0;
                if (!string.IsNullOrEmpty(pubDateStr) &&
                    DateTimeOffset.TryParse(pubDateStr, out var dto))
                {
                    pubTime = dto.ToUnixTimeSeconds();
                }
                rowTable["pub_time"] = pubTime;

                // category
                rowTable["category"] = item.Element("category")?.Value ?? "";

                // author
                rowTable["author"] = item.Element("author")?.Value ?? "";

                // Insert row into result array
                _lua["__temp_val"] = rowTable;
                _lua["__temp_tbl"] = resultTable;
                _lua.DoString($"__temp_tbl[{rowIndex}] = __temp_val");
                _lua["__temp_val"] = null;
                _lua["__temp_tbl"] = null;
            }

            return resultTable;
        }
        catch (Exception ex)
        {
            SetLastError($"GetRSS failed: {ex.GetBaseException().Message}");
            return null;
        }
    }

    // ─────────────────────────────────────────────
    //  Generic File Download
    // ─────────────────────────────────────────────

    /// <summary>
    /// Download a URL to a local file. Uses the session-cookie-bearing HTTP client,
    /// so it works for authenticated PT-site URLs (e.g. torrent download links).
    /// Creates parent directories automatically.
    /// Returns true on success, nil on failure.
    /// </summary>
    public object? DownloadFile(string url, string destinationPath)
    {
        try
        {
            var response = GetAsync(url).Result;
            response.EnsureSuccessStatusCode();
            var bytes = response.Content.ReadAsByteArrayAsync().Result;

            if (bytes.Length == 0)
            {
                SetLastError($"DownloadFile: Received empty response from {url}");
                return null;
            }

            var dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllBytes(destinationPath, bytes);
            return true;
        }
        catch (Exception ex)
        {
            SetLastError($"DownloadFile failed: {ex.GetBaseException().Message}");
            return null;
        }
    }
}

