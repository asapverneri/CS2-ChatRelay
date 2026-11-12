using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Events;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json.Serialization;

namespace ChatRelay;

public class ChatRelayConfig : BasePluginConfig
{
    [JsonPropertyName("IgnoreCommands")] public bool IgnoreCommands { get; set; } = true;

    [JsonPropertyName("OnlyShowCommands")] public bool OnlyShowCommands { get; set; } = false;

    [JsonPropertyName("DiscordWebhook")] public string DiscordWebhook { get; set; } = "";

    [JsonPropertyName("JoinMessages")] public bool JoinMessages { get; set; } = false;

    [JsonPropertyName("LeaveMessages")] public bool LeaveMessages { get; set; } = false;

    // Empty = use DiscordWebhook
    [JsonPropertyName("ConnectionWebhook")] public string ConnectionWebhook { get; set; } = "";

    [JsonPropertyName("EmbedFooter")] public string EmbedFooter { get; set; } = "ChatRelay (1.1) by verneri";

}
public class ChatRelay : BasePlugin, IPluginConfig<ChatRelayConfig>
{
    public override string ModuleName => "ChatRelay";
    public override string ModuleDescription => "send your cs2 server chat messages to discord";
    public override string ModuleAuthor => "verneri";
    public override string ModuleVersion => "1.2";

    public ChatRelayConfig Config { get; set; } = new();

    public void OnConfigParsed(ChatRelayConfig config)
    {
        Config = config;
    }
    public override void Load(bool hotReload)
    {
        Logger.LogInformation($"loaded successfully! (Version {ModuleVersion})");
        RegisterEventHandler<EventPlayerChat>(OnEventPlayerChat);
        RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        RegisterListener<Listeners.OnMapStart>(OnMapStart);

        // hot reload: players already online must not be announced as new
        foreach (var p in Utilities.GetPlayers())
            if (IsHuman(p)) _online[p.SteamID] = p.PlayerName;

        if ((Config.JoinMessages || Config.LeaveMessages) && string.IsNullOrWhiteSpace(ConnectionWebhookUrl))
            Logger.LogWarning("JoinMessages/LeaveMessages enabled but no ConnectionWebhook or DiscordWebhook is set");
    }

    // SteamID -> name of humans currently announced as online
    readonly Dictionary<ulong, string> _online = new();

    string ConnectionWebhookUrl =>
        string.IsNullOrWhiteSpace(Config.ConnectionWebhook) ? Config.DiscordWebhook : Config.ConnectionWebhook;

    static bool IsHuman(CCSPlayerController? p) =>
        p != null && p.IsValid && !p.IsBot && !p.IsHLTV && p.SteamID != 0;

    public HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (!IsHuman(player)) return HookResult.Continue;

        // player_connect_full fires again for everyone after a map change; only announce new players
        var isNew = !_online.ContainsKey(player!.SteamID);
        _online[player.SteamID] = player.PlayerName;
        if (isNew && Config.JoinMessages)
            _ = SendConnectionMessage(player.PlayerName, player.SteamID, true);

        return HookResult.Continue;
    }

    public HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (!IsHuman(player)) return HookResult.Continue;

        // 54/55/57 = loop shutdown/deactivate/level-load: map change, the player comes back
        if (@event.Reason is 54 or 55 or 57) return HookResult.Continue;

        // only announce leaving for players that were announced joining (skips failed/never-finished connects)
        if (_online.Remove(player!.SteamID) && Config.LeaveMessages)
            _ = SendConnectionMessage(player.PlayerName, player.SteamID, false);

        return HookResult.Continue;
    }

    void OnMapStart(string mapName)
    {
        // players who dropped during the map change never get a real disconnect event
        AddTimer(90f, () =>
        {
            var connected = Utilities.GetPlayers().Where(IsHuman).Select(p => p.SteamID).ToHashSet();
            foreach (var (id, name) in _online.Where(kv => !connected.Contains(kv.Key)).ToList())
            {
                _online.Remove(id);
                if (Config.LeaveMessages) _ = SendConnectionMessage(name, id, false);
            }
        });
    }

    public HookResult OnEventPlayerChat(EventPlayerChat @event, GameEventInfo info)
    {
        var eventplayer = @event.Userid;
        var player = Utilities.GetPlayerFromUserid(eventplayer);
        if (player == null || !player.IsValid ||  @event.Text == null)
            return HookResult.Continue;

        if (Config.IgnoreCommands)
        {
            if (@event.Text.Contains('!') || @event.Text.Contains('/'))
                return HookResult.Continue;
        }

        if (Config.OnlyShowCommands)
        {
            if (!@event.Text.Contains('!') && !@event.Text.Contains('/'))
                return HookResult.Continue;
        }

        string playerteam = "[ALL]";
        if (@event.Teamonly)
        {
            switch ((CsTeam)player.TeamNum)
            {
                case CsTeam.Terrorist:
                    playerteam = "[T]";
                    break;
                case CsTeam.CounterTerrorist:
                    playerteam = "[CT]";
                    break;
                case CsTeam.Spectator:
                    playerteam = "[SPEC]";
                    break;
                case CsTeam.None:
                    playerteam = "[NONE]";
                    break;
            }
        }


        _ = SendWebhookMessageAsEmbed(player.PlayerName, @event.Text, playerteam, player.SteamID);
        return HookResult.Continue;
    }

    static readonly HttpClient Http = new();

    Task SendConnectionMessage(string playerName, ulong steamID, bool joined) =>
        PostEmbed(ConnectionWebhookUrl, playerName, steamID, joined ? "joined the server" : "left the server",
            joined ? 5763719 : 15548997);

    public Task SendWebhookMessageAsEmbed(string playerName, string msg, string playerteam, ulong steamID) =>
        PostEmbed(Config.DiscordWebhook, playerName, steamID, $"{playerteam}: {msg}", 16760667);

    async Task PostEmbed(string webhook, string title, ulong steamID, string description, int color)
    {
        if (string.IsNullOrWhiteSpace(webhook)) return;

        var embed = new
        {
            title,
            url = $"https://steamcommunity.com/profiles/{steamID}",
            description,
            color,
            footer = new { text = $"{Config.EmbedFooter}" }
        };

        var payload = new { embeds = new[] { embed }, allowed_mentions = new { parse = Array.Empty<string>() } };
        var content = new StringContent(Newtonsoft.Json.JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

        try
        {
            var response = await Http.PostAsync(webhook, content);
            if (!response.IsSuccessStatusCode)
                Logger.LogInformation($"Error while sending message to Discord! code: {response.StatusCode}");
        }
        catch (Exception ex)
        {
            Logger.LogInformation($"Error while sending message to Discord! {ex.Message}");
        }
    }

}
