using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json.Serialization;

namespace ChatRelay;

public class ChatRelayConfig : BasePluginConfig
{
    [JsonPropertyName("IgnoreCommands")] public bool IgnoreCommands { get; set; } = true;

    [JsonPropertyName("OnlyShowCommands")] public bool OnlyShowCommands { get; set; } = false;

    [JsonPropertyName("DiscordWebhook")] public string DiscordWebhook { get; set; } = "";

    [JsonPropertyName("EmbedFooter")] public string EmbedFooter { get; set; } = "ChatRelay (1.1) by verneri";

}
public class ChatRelay : BasePlugin, IPluginConfig<ChatRelayConfig>
{
    public override string ModuleName => "ChatRelay";
    public override string ModuleDescription => "send your cs2 server chat messages to discord";
    public override string ModuleAuthor => "verneri";
    public override string ModuleVersion => "1.1";

    public ChatRelayConfig Config { get; set; } = new();

    public void OnConfigParsed(ChatRelayConfig config)
    {
        Config = config;
    }
    public override void Load(bool hotReload)
    {
        Logger.LogInformation($"loaded successfully! (Version {ModuleVersion})");
        RegisterEventHandler<EventPlayerChat>(OnEventPlayerChat);
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

    public async Task SendWebhookMessageAsEmbed(string playerName, string msg, string playerteam, ulong steamID)
    {
        using (var httpClient = new HttpClient())
        {
            var embed = new
            {
                title = playerName,
                url = $"https://steamcommunity.com/profiles/{steamID}",
                description = $"{playerteam}: {msg}",
                color = 16760667,
                footer = new
                {
                    text = $"{Config.EmbedFooter}"
                }
            };

            var payload = new
            {
                embeds = new[] { embed }
            };

            var jsonPayload = Newtonsoft.Json.JsonConvert.SerializeObject(payload);
            var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync(Config.DiscordWebhook, content);

            if (!response.IsSuccessStatusCode)
            {
                Logger.LogInformation($"Error while sending message to Discord! code: {response.StatusCode}");
            }
        }
    }

}