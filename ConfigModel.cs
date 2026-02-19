using System.Text.Json.Serialization;

namespace QBManager;

public class AppConfig
{
    [JsonPropertyName("server")]
    public ServerConfig Server { get; set; } = new();

    [JsonPropertyName("scripts")]
    public string[] Scripts { get; set; } = [];

    [JsonPropertyName("settings")]
    public AppSettings Settings { get; set; } = new();

    [JsonPropertyName("database_path")]
    public string? DatabasePath { get; set; }
}

public class ServerConfig
{
    [JsonPropertyName("base_url")]
    public string BaseUrl { get; set; } = "http://localhost:8080";

    [JsonPropertyName("username")]
    public string Username { get; set; } = "admin";

    [JsonPropertyName("password")]
    public string Password { get; set; } = "admin_password";
}

public class AppSettings
{
    [JsonPropertyName("script_interval_seconds")]
    public int ScriptIntervalSeconds { get; set; } = 5;

    [JsonPropertyName("loop_interval_seconds")]
    public int LoopIntervalSeconds { get; set; } = 300;

    [JsonPropertyName("max_retry_attempts")]
    public int MaxRetryAttempts { get; set; } = 3;
}
