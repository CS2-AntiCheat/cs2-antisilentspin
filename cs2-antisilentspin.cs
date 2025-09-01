using System.Net.Http.Json;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.ValveConstants.Protobuf;
using Microsoft.Extensions.Localization;
using static CounterStrikeSharp.API.Core.Listeners;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;
namespace AntiSilentSpin;

public class Config : BasePluginConfig
{
    [JsonPropertyName("MaxSuspicion")] public int MaxSuspicion { get; set; } = 5;
    [JsonPropertyName("AngleThreshold")] public float AngleThreshold { get; set; } = 50f;
    [JsonPropertyName("AngularSpeedThreshold")] public float AngularSpeedThreshold { get; set; } = 400f;
    [JsonPropertyName("PunishmentType (PrintAll, PrintAdmin, Kick, Ban)")] public string PunishmentType { get; set; } = "PrintAdmin";
    [JsonPropertyName("BanTimeInMinutes")] public int BanTimeInMinutes { get; set; } = 0;
    [JsonPropertyName("DiscordWebhookUrl")] public string DiscordWebhookUrl { get; set; } = string.Empty;
    [JsonPropertyName("WebhookSettings")] public WebhookConfig Webhook { get; set; } = new();
}

public class WebhookConfig
{
    [JsonPropertyName("Title")] public string Title { get; set; } = "🚨 Cheat Detected";
    [JsonPropertyName("Color")] public string ColorHex { get; set; } = "#FF0000";
    [JsonPropertyName("Footer")] public string Footer { get; set; } = "Anti-Cheat System";
    [JsonPropertyName("Username")] public string Username { get; set; } = "Anti-Cheat Bot";
    [JsonPropertyName("AvatarUrl")] public string AvatarUrl { get; set; } = "";
    [JsonPropertyName("ThumbnailUrl")] public string ThumbnailUrl { get; set; } = "";
    [JsonPropertyName("ImageUrl")] public string ImageUrl { get; set; } = "";
}

public class AntiSilentSpin : BasePlugin, IPluginConfig<Config>
{
    public override string ModuleName => "Anti Silent & Spinbot";
    public override string ModuleVersion => "v2";
    public override string ModuleAuthor => "schwarper";
    public override string ModuleDescription => "Detects SilentAim and Spinbot cheats.";

    private sealed class PlayerState
    {
        public QAngle LastAngle { get; set; } = new(0, 0, 0);
        public float LastTickTime { get; set; } = 0;
        public bool JustKilled { get; set; } = false;
        public QAngle LastAngleOnKill { get; set; } = new(0, 0, 0);
        public float LastKillSilentAngle { get; set; } = 0;
        public int SuspicionCount { get; set; } = 0;
    }

    private readonly Dictionary<CCSPlayerController, PlayerState> _playerStates = [];
    public Config Config { get; set; } = new();

    public override void Load(bool hotReload)
    {
        if (hotReload)
        {
            foreach (CCSPlayerController? player in Utilities.GetPlayers().Where(p => p is { IsBot: false }))
            {
                _playerStates[player] = new PlayerState();
            }
        }
    }

    public void OnConfigParsed(Config config)
    {
        Config = config;
    }

    [GameEventHandler]
    public HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        CCSPlayerController? player = @event.Userid;
        if (player != null && !player.IsBot)
        {
            _playerStates[player] = new PlayerState();
        }
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        CCSPlayerController? player = @event.Userid;
        if (player != null && !player.IsBot)
        {
            _playerStates.Remove(player);
        }
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        CCSPlayerController? attacker = @event.Attacker;
        CCSPlayerController? victim = @event.Userid;

        if (attacker == null || victim == null || attacker == victim || !attacker.IsValid || attacker.IsBot)
            return HookResult.Continue;

        CCSPlayerPawn? attackerPawn = attacker.PlayerPawn?.Value;
        CCSPlayerPawn? victimPawn = victim.PlayerPawn?.Value;

        if (attackerPawn == null || victimPawn == null)
            return HookResult.Continue;

        if (!_playerStates.TryGetValue(attacker, out PlayerState? attackerState))
        {
            attackerState = new PlayerState();
            _playerStates[attacker] = attackerState;
        }

        attackerState.JustKilled = true;
        attackerState.LastAngleOnKill = attackerPawn.EyeAngles;
        attackerState.LastKillSilentAngle = CalculateAngleBetween(attackerPawn.EyeAngles, attackerPawn.AbsOrigin!.ToVector3(), victimPawn.AbsOrigin!.ToVector3());

        return HookResult.Continue;
    }

    [ListenerHandler<OnTick>]
    public void OnTick()
    {
        float currentTime = Server.CurrentTime;

        foreach ((CCSPlayerController player, PlayerState playerState) in _playerStates)
        {
            if (!player.IsValid || !playerState.JustKilled)
                continue;

            CCSPlayerPawn? playerPawn = player.PlayerPawn?.Value;
            if (playerPawn == null) continue;

            float deltaTime = currentTime - playerState.LastTickTime;
            if (deltaTime <= 0.001f) continue;

            bool suspiciousActionDetected = false;
            string reason = string.Empty;

            QAngle currentEyeAngles = playerPawn.EyeAngles;
            float angleDelta = CalculateAngleDifference2D(playerState.LastAngleOnKill, currentEyeAngles);
            float angularSpeed = angleDelta / deltaTime;

            if (angularSpeed > Config.AngularSpeedThreshold)
            {
                suspiciousActionDetected = true;
                reason = $"High angular speed: {angularSpeed:F0}°/s";
            }
            else if (playerState.LastKillSilentAngle > Config.AngleThreshold)
            {
                suspiciousActionDetected = true;
                reason = $"Silent Aim suspicion: {playerState.LastKillSilentAngle:F1}°";
            }

            if (suspiciousActionDetected)
            {
                AddSuspicion(player, playerState, reason);
            }

            playerState.LastAngle = currentEyeAngles;
            playerState.LastTickTime = currentTime;
            playerState.JustKilled = false;
        }
    }

    private void AddSuspicion(CCSPlayerController player, PlayerState playerState, string reason)
    {
        playerState.SuspicionCount++;

        if (playerState.SuspicionCount >= Config.MaxSuspicion)
        {
            playerState.SuspicionCount = 0;

            LocalizedString detectionReason = Localizer["Suspicious behavior detected", player.PlayerName];

            switch (Config.PunishmentType.ToLower())
            {
                case "printall":
                    Server.PrintToChatAll(detectionReason);
                    break;
                case "printadmin":
                    PrintToAdmins(detectionReason);
                    break;
                case "kick":
                    player.Disconnect(NetworkDisconnectionReason.NETWORK_DISCONNECT_KICKED_VACNETABNORMALBEHAVIOR);
                    break;
                case "ban":
                    Server.ExecuteCommand($"mm_ban {player.UserId} {Config.BanTimeInMinutes} \"{detectionReason}\"");
                    Server.ExecuteCommand($"css_ban {player.UserId} {Config.BanTimeInMinutes} \"{detectionReason}\"");
                    break;
            }

            if (!string.IsNullOrWhiteSpace(Config.DiscordWebhookUrl))
            {
                string description = $"**Server IP:** {ServerIP.Get()}\n" +
                                     $"**Player:** {player.PlayerName}\n" +
                                     $"**SteamID:** {player.SteamID}\n" +
                                     $"**Cheat Type:** Silent Aim / Spinbot\n" +
                                     $"**Detail:** {reason}";

                _ = Task.Run(() => DiscordNotifier.SendDiscordEmbedAsync(Config.DiscordWebhookUrl, Config.Webhook, description));
            }
        }
    }

    public static void PrintToAdmins(string message)
    {
        List<CCSPlayerController> players = Utilities.GetPlayers();
        foreach (CCSPlayerController player in players)
        {
            if (player.IsBot)
                continue;

            if (!AdminManager.PlayerHasPermissions(player, "@css/ban"))
                continue;

            player.PrintToChat(message);
        }
    }

    private static float CalculateAngleBetween(QAngle eyeAngles, Vector3 sourcePosition, Vector3 targetPosition)
    {
        Vector3 forwardVector = AngleToForwardVector(eyeAngles);
        Vector3 directionToTarget = Vector3.Normalize(targetPosition - sourcePosition);
        float dotProduct = Vector3.Dot(forwardVector, directionToTarget);

        return MathF.Acos(Math.Clamp(dotProduct, -1f, 1f)) * (180f / MathF.PI);
    }

    private static float CalculateAngleDifference2D(QAngle angleA, QAngle angleB)
    {
        float pitchDifference = NormalizeAngleSigned(angleA.X - angleB.X);
        float yawDifference = NormalizeAngleSigned(angleA.Y - angleB.Y);
        return MathF.Sqrt((pitchDifference * pitchDifference) + (yawDifference * yawDifference));
    }

    private static Vector3 AngleToForwardVector(QAngle angles)
    {
        float pitchRad = angles.X * (MathF.PI / 180f);
        float yawRad = angles.Y * (MathF.PI / 180f);
        float cosPitch = MathF.Cos(pitchRad);

        return new Vector3(
            cosPitch * MathF.Cos(yawRad),
            cosPitch * MathF.Sin(yawRad),
            -MathF.Sin(pitchRad)
        );
    }

    private static float NormalizeAngleSigned(float angle)
    {
        angle %= 360f;
        return angle > 180f ? angle - 360f : angle < -180f ? angle + 360f : angle;
    }
}

public static class VectorExtensions
{
    public static Vector3 ToVector3(this Vector v)
    {
        return new(v.X, v.Y, v.Z);
    }
}

public static class ServerIP
{
    private delegate nint CNetworkSystem_UpdatePublicIp(nint a1);
    private static CNetworkSystem_UpdatePublicIp? _networkSystemUpdatePublicIp;
    private static string? _port;

    public static string Get()
    {
        if (string.IsNullOrEmpty(_port))
        {
            _port = ConVar.Find("hostport")?.GetPrimitiveValue<int>().ToString();
        }

        nint _networkSystem = NativeAPI.GetValveInterface(0, "NetworkSystemVersion001");

        unsafe
        {
            if (_networkSystemUpdatePublicIp == null)
            {
                nint funcPtr = *(nint*)(*(nint*)_networkSystem + 256);
                _networkSystemUpdatePublicIp = Marshal.GetDelegateForFunctionPointer<CNetworkSystem_UpdatePublicIp>(funcPtr);
            }

            byte* ipBytes = (byte*)(_networkSystemUpdatePublicIp(_networkSystem) + 4);
            return $"{ipBytes[0]}.{ipBytes[1]}.{ipBytes[2]}.{ipBytes[3]}:{_port}";
        }
    }
}

public static class DiscordNotifier
{
    private static readonly HttpClient HttpClient = new();

    public static async Task SendDiscordEmbedAsync(string webhookUrl, WebhookConfig webhookConfig, string? description = null, string? title = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(webhookUrl)) return;

            int color = int.Parse(webhookConfig.ColorHex.Replace("#", ""), System.Globalization.NumberStyles.HexNumber);

            Dictionary<string, object?> embed = new()
            {
                ["title"] = string.IsNullOrWhiteSpace(title) ? webhookConfig.Title : title,
                ["description"] = description,
                ["color"] = color,
                ["thumbnail"] = string.IsNullOrWhiteSpace(webhookConfig.ThumbnailUrl) ? null : new { url = webhookConfig.ThumbnailUrl },
                ["image"] = string.IsNullOrWhiteSpace(webhookConfig.ImageUrl) ? null : new { url = webhookConfig.ImageUrl },
                ["footer"] = new { text = webhookConfig.Footer },
                ["timestamp"] = DateTime.UtcNow.ToString("o")
            };

            var payload = new
            {
                username = webhookConfig.Username,
                avatar_url = webhookConfig.AvatarUrl,
                embeds = new[] { embed }
            };

            using HttpResponseMessage response = await HttpClient.PostAsJsonAsync(webhookUrl, payload);
            if (!response.IsSuccessStatusCode)
            {
                string errorContent = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"[DiscordNotifier] Discord API Error: {response.StatusCode} - {errorContent}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DiscordNotifier] An error occurred while sending the notification: {ex.Message}");
        }
    }
}