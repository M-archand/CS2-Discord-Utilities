
using System.Text;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Timers;
using DiscordUtilitiesAPI;
using DiscordUtilitiesAPI.Builders;
using DiscordUtilitiesAPI.Events;
using DiscordUtilitiesAPI.Helpers;
using static CounterStrikeSharp.API.Core.Listeners;
using CssTimer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace Report
{
    public partial class Report : BasePlugin, IPluginConfig<Config>
    {
        private const float PendingReportTimeoutSeconds = 120.0f;

        public override string ModuleName => "[Discord Utilities] Report System";
        public override string ModuleAuthor => "SourceFactory.eu";
        public override string ModuleVersion => "1.4";
        public IDiscordUtilitiesAPI? DiscordUtilities { get; set; }
        public Config Config { get; set; } = new();
        private readonly Dictionary<ulong, PendingReportState> pendingReports = new();
        public Dictionary<ulong, int> reportCooldowns = new();
        public Dictionary<string, ReportData> reportsList = new();
        public List<ulong> solvedPlayers = new();

        private sealed class PendingReportState
        {
            public required ulong TargetSteamId { get; init; }
            public CssTimer? TimeoutTimer { get; set; }
        }

        public class ReportData
        {
            public required ulong senderSteamId;
            public required string senderName;
            public required ulong targetSteamId;
            public required string targetName;
            public required string reason;
            public required ulong messageId;
            public required DateTime time;
        }
        public void OnConfigParsed(Config config)
        {
            Config = config;
        }
        public override void OnAllPluginsLoaded(bool hotReload)
        {
            var eventSender = GetDiscordUtilitiesEventSender();
            eventSender.DiscordUtilitiesEventHandlers -= DiscordUtilitiesEventHandler;
            eventSender.DiscordUtilitiesEventHandlers += DiscordUtilitiesEventHandler;
            DiscordUtilities!.CheckVersion(ModuleName, ModuleVersion);
        }
        public override void Unload(bool hotReload)
        {
            ClearPendingReports(notifyPlayers: hotReload);
            GetDiscordUtilitiesEventSender().DiscordUtilitiesEventHandlers -= DiscordUtilitiesEventHandler;
        }

        public override void Load(bool hotReload)
        {
            CreateCustomCommands();
            AddCommandListener(null!, OnPlayerSay, HookMode.Pre);
            RegisterListener<OnMapStart>(mapName =>
            {
                ClearPendingReports(notifyPlayers: true);
                solvedPlayers.Clear();
            });
        }

        [GameEventHandler]
        public HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
        {
            if (reportsList.Count == 0 || Config.ReportExpiration == 0)
                return HookResult.Continue;

            var expiredReportIds = reportsList
                .Where(report => (DateTime.Now - report.Value.time).TotalMinutes > Config.ReportExpiration)
                .Select(report => report.Key)
                .ToList();

            foreach (var reportId in expiredReportIds)
            {
                PerformReportSolved(reportId, 1, null);
            }
            return HookResult.Continue;
        }

        [GameEventHandler(HookMode.Pre)]
        public HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
        {
            var player = @event.Userid;
            if (player != null && player.IsValid)
            {
                CancelPendingReport(player.SteamID, notifyPlayer: false);
                reportCooldowns.Remove(player.SteamID);
            }

            return HookResult.Continue;
        }


        private HookResult OnPlayerSay(CCSPlayerController? player, CommandInfo info)
        {
            if (player == null || !player.IsValid || player.AuthorizedSteamID == null)
                return HookResult.Continue;

            var command = info.GetArg(0).Trim().ToLowerInvariant();
            if (command is not ("say" or "say_team"))
                return HookResult.Continue;

            if (!pendingReports.TryGetValue(player.SteamID, out var pendingReport))
                return HookResult.Continue;

            var reason = ExtractChatMessage(info);
            if (reason.Length < Config.ReasonLength)
            {
                player.PrintToChat($"{Localizer["Chat.Prefix"]} {Localizer[name: "Chat.ReportShortReason"]}");
                ResetPendingReportTimeout(player.SteamID);
                return HookResult.Stop;
            }
            if (IsCancelReasonInput(reason))
            {
                CancelPendingReport(player.SteamID, notifyPlayer: true);
                return HookResult.Stop;
            }

            var blockedReasons = Config.BlockedReason.Trim().ToLower().Split(",").ToList();
            if (blockedReasons.Count > 0)
            {
                var reasonsList = reason.ToLower().Split(" ").ToList();
                foreach (var x in reasonsList)
                {
                    if (blockedReasons.Contains(x))
                    {
                        player.PrintToChat($"{Localizer["Chat.Prefix"]} {Localizer["Chat.ReportBlockedReason", x]}");
                        ResetPendingReportTimeout(player.SteamID);
                        return HookResult.Stop;
                    }
                }
            }

            var target = Utilities.GetPlayerFromSteamId(pendingReport.TargetSteamId);
            if (target == null || !target.IsValid || target.AuthorizedSteamID == null)
            {
                player.PrintToChat($"{Localizer["Chat.Prefix"]} {Localizer["Chat.TargetNotConnected"]}");
                CancelPendingReport(player.SteamID, notifyPlayer: false);
                return HookResult.Stop;
            }

            if (SendReport(player, target, reason))
                CancelPendingReport(player.SteamID, notifyPlayer: false);
            else
                ResetPendingReportTimeout(player.SteamID);

            return HookResult.Stop;
        }

        public bool SendReport(CCSPlayerController sender, CCSPlayerController? target, string reason)
        {
            if (reportCooldowns.ContainsKey(sender.SteamID))
            {
                var remainingTime = (int)Server.CurrentTime - reportCooldowns[sender.SteamID];
                if (remainingTime < Config.ReportCooldown)
                {
                    sender.PrintToChat($"{Localizer["Chat.Prefix"]} {Localizer["Chat.ReportCooldown", Config.ReportCooldown]}");
                    return false;
                }
                reportCooldowns.Remove(sender.SteamID);
            }
            if (target != null && Config.AntiSpamReport && solvedPlayers.Contains(target.SteamID))
            {
                sender.PrintToChat($"{Localizer["Chat.Prefix"]} {Localizer["Chat.ThisPlayerCannotBeReported", target.PlayerName]}");
                return false;
            }
            if (Config.ReportMethod != 3)
            {
                if (target == null || !target.IsValid || target.AuthorizedSteamID == null)
                {
                    sender.PrintToChat($"{Localizer["Chat.Prefix"]} {Localizer["Chat.TargetNotConnected"]}");
                    return false;
                }
                if (!Config.SelfReport)
                {
                    if (target == sender)
                    {
                        sender.PrintToChat($"{Localizer["Chat.Prefix"]} {Localizer["Chat.SelfReport"]}");
                        return false;
                    }
                }
            }

            var replaceVariablesBuilder = new ReplaceVariables.Builder
            {
                ServerData = true,
                PlayerData = sender,
                TargetData = Config.ReportMethod != 3 ? target : sender,
                CustomVariables = new Dictionary<string, string>{
                    { "{REASON}", reason }
                },
            };

            var config = Config.ReportEmbed;
            var embedBuider = DiscordUtilities!.GetEmbedBuilderFromConfig(config, replaceVariablesBuilder);
            var content = DiscordUtilities!.ReplaceVariables(Config.ReportEmbed.Content, replaceVariablesBuilder);
            var reportTarget = Config.ReportMethod != 3 ? target! : sender;

            var reportData = new ReportData()
            {
                senderSteamId = sender.SteamID,
                senderName = sender.PlayerName,
                targetSteamId = reportTarget.SteamID,
                targetName = reportTarget.PlayerName,
                reason = reason,
                messageId = 0,
                time = DateTime.Now
            };

            string reportId = GetRandomChars();
            reportsList.Add(reportId, reportData);
            DiscordUtilities.SendCustomMessageToChannel($"report_{reportId}", ulong.Parse(Config.ChannelID), content, embedBuider, null, true);

            reportCooldowns[sender.SteamID] = (int)Server.CurrentTime;
            sender.PrintToChat($"{Localizer["Chat.Prefix"]} {Localizer["Chat.ReportSend", target?.PlayerName ?? sender.PlayerName, reason]}");
            foreach (var admin in Utilities.GetPlayers().Where(p => !p.IsBot && !p.IsHLTV && AdminManager.PlayerHasPermissions(p, Config.AdminFlag)))
            {
                admin.PrintToChat(Localizer["Chat.AdminReportSend", sender.PlayerName, target?.PlayerName ?? sender.PlayerName, reason]);
            }

            return true;
        }
        public void CustomReasonReport(CCSPlayerController sender, CCSPlayerController target)
        {
            StartPendingReport(sender, target);
            sender.PrintToChat($"{Localizer["Chat.Prefix"]} {Localizer["Chat.InserYourReason"]}");
        }
        private CCSPlayerController? GetTargetByName(string name, CCSPlayerController player)
        {
            int matchingCount = 0;
            CCSPlayerController? target = null;

            foreach (var p in Utilities.GetPlayers().Where(p => p.IsValid && p.SteamID.ToString().Length == 17 && !AdminManager.PlayerHasPermissions(p, Config.UnreportableFlag)))
            {
                if (p.PlayerName.Contains(name))
                {
                    target = p;
                    matchingCount++;
                }
            }

            if (matchingCount == 0)
            {
                player.PrintToChat($"{Localizer["Chat.Prefix"]} {Localizer["Chat.TargetNotFound", name]}");
                return null;
            }
            if (matchingCount > 1)
            {
                player.PrintToChat($"{Localizer["Chat.Prefix"]} {Localizer["Chat.MultipleTargetFound", name]}");
                return null;
            }

            return target;
        }

        private int GetTargetsForReportCount(CCSPlayerController player)
        {
            return Utilities.GetPlayers().Where(p => p.IsValid && p != player && !p.IsHLTV && !p.IsBot && p.Connected == PlayerConnectedState.PlayerConnected && p.SteamID.ToString().Length == 17 && !AdminManager.PlayerHasPermissions(p, Config.UnreportableFlag)).Count();
        }

        private void OnInteractionCreated(InteractionData interaction, UserData user)
        {
            var CustomId = interaction.CustomId;
            if (CustomId.Contains("report_"))
            {
                if (!string.IsNullOrEmpty(Config.ReportEmbed.ReportButton.AdminRolesId))
                {
                    var requiredRoles = Config.ReportEmbed.ReportButton.AdminRolesId.Trim().Split(',').ToList();
                    if (requiredRoles != null && requiredRoles.Count > 0)
                    {
                        bool hasPermission = requiredRoles.Count(role => user.RolesIds.Contains(ulong.Parse(role))) > 0;
                        if (!hasPermission)
                        {
                            var config = Config.ReportEmbed.ReportButton.ReportReplyEmbeds.ReportFailed;
                            var embedBuider = DiscordUtilities!.GetEmbedBuilderFromConfig(config, null);
                            string content = Config.ReportEmbed.ReportButton.ReportReplyEmbeds.ReportFailed.Content;
                            DiscordUtilities.SendRespondMessageToInteraction(interaction.InteractionId, content, embedBuider, null, false, Config.ReportEmbed.ReportButton.ReportReplyEmbeds.ReportFailed.SilentResponse);
                            return;
                        }
                    }
                }

                CustomId = CustomId.Replace("report_", "");
                var messageBuilders = interaction.Builders;
                if (messageBuilders != null && messageBuilders.Embeds != null && messageBuilders.Embeds.Count() > 0)
                {
                    if (!reportsList.ContainsKey(CustomId))
                        return;

                    var data = reportsList[CustomId];
                    ReportSolvedAction(data.senderSteamId, data.targetName, data.targetSteamId);
                    var messageId = data.messageId;
                    reportsList.Remove(CustomId);

                    var replaceVariablesBuilder = new ReplaceVariables.Builder
                    {
                        DiscordUser = user,
                    };

                    var embedsBuilder = messageBuilders.Embeds.FirstOrDefault();
                    if (embedsBuilder != null)
                    {
                        embedsBuilder.Title = string.IsNullOrEmpty(Config.SolvedEmbeds.SolvedReportEmbed.Title) ? embedsBuilder.Title : DiscordUtilities!.ReplaceVariables(Config.SolvedEmbeds.SolvedReportEmbed.Title, replaceVariablesBuilder);
                        embedsBuilder.ThumbnailUrl = string.IsNullOrEmpty(Config.SolvedEmbeds.SolvedReportEmbed.Thumbnail) ? embedsBuilder.ThumbnailUrl : Config.SolvedEmbeds.SolvedReportEmbed.Thumbnail;
                        embedsBuilder.ImageUrl = string.IsNullOrEmpty(Config.SolvedEmbeds.SolvedReportEmbed.Image) ? embedsBuilder.ImageUrl : Config.SolvedEmbeds.SolvedReportEmbed.Image;
                        embedsBuilder.Color = Config.SolvedEmbeds.SolvedReportEmbed.Color;
                        embedsBuilder.Footer = string.IsNullOrEmpty(Config.SolvedEmbeds.SolvedReportEmbed.Footer) ? embedsBuilder.Footer : DiscordUtilities!.ReplaceVariables(Config.SolvedEmbeds.SolvedReportEmbed.Footer, replaceVariablesBuilder);
                        embedsBuilder.FooterTimestamp = Config.SolvedEmbeds.SolvedReportEmbed.FooterTimestamp;
                    }
                    var content = messageBuilders.Content;
                    if (content != null)
                        content = string.IsNullOrEmpty(Config.SolvedEmbeds.SolvedReportEmbed.Content) ? content : DiscordUtilities!.ReplaceVariables(Config.SolvedEmbeds.SolvedReportEmbed.Content, replaceVariablesBuilder);

                    DiscordUtilities!.UpdateMessage(messageId, interaction.ChannelID, content, embedsBuilder, null);

                    var config = Config.ReportEmbed.ReportButton.ReportReplyEmbeds.ReportSucces;
                    embedsBuilder = DiscordUtilities!.GetEmbedBuilderFromConfig(config, null);
                    content = Config.ReportEmbed.ReportButton.ReportReplyEmbeds.ReportSucces.Content;
                    DiscordUtilities.SendRespondMessageToInteraction(interaction.InteractionId, content, embedsBuilder, null, false, Config.ReportEmbed.ReportButton.ReportReplyEmbeds.ReportSucces.SilentResponse);
                }
            }
        }

        public void ReportSolvedAction(ulong senderSteamId, string targetName, ulong targetSteamId)
        {
            if (Config.SendMessageOnSolved)
            {
                var sender = Utilities.GetPlayerFromSteamId(senderSteamId);
                if (sender != null && sender.IsValid)
                    sender.PrintToChat($"{Localizer["Chat.Prefix"]} {Localizer["Chat.YourReportHasBeenSolved", targetName]}");
            }
            if (Config.AntiSpamReport)
            {
                if (!solvedPlayers.Contains(targetSteamId))
                    solvedPlayers.Add(targetSteamId);
            }
        }

        public void PerformReportSolved(string reportId, int solvedType, CCSPlayerController? player)
        {
            if (!reportsList.ContainsKey(reportId))
                return;

            var data = reportsList[reportId];
            ReportSolvedAction(data.senderSteamId, data.targetName, data.targetSteamId);
            //if (player != null)
            reportsList.Remove(reportId);

            if (DiscordUtilities!.IsCustomMessageSaved(data.messageId))
            {
                var message = DiscordUtilities.GetMessageDataFromCustomMessage(data.messageId);
                DiscordUtilities.RemoveSavedCustomMessage(data.messageId);
                if (message == null)
                    return;

                var messageBuilders = message.Builders;
                if (messageBuilders != null && messageBuilders.Embeds != null && messageBuilders.Embeds.Count() > 0)
                {
                    var replaceVariablesBuilder = new ReplaceVariables.Builder
                    {
                        ServerData = true,
                        PlayerData = solvedType == 0 ? player : null,
                    };

                    var embedsBuilder = messageBuilders.Embeds.FirstOrDefault();
                    var content = messageBuilders.Content;
                    if (embedsBuilder != null)
                    {
                        if (solvedType == 0)
                        {
                            embedsBuilder.Title = string.IsNullOrEmpty(Config.SolvedEmbeds.IngameSolvedReportEmbed.Title) ? embedsBuilder.Title : DiscordUtilities.ReplaceVariables(Config.SolvedEmbeds.IngameSolvedReportEmbed.Title, replaceVariablesBuilder);
                            embedsBuilder.ThumbnailUrl = string.IsNullOrEmpty(Config.SolvedEmbeds.IngameSolvedReportEmbed.Thumbnail) ? embedsBuilder.ThumbnailUrl : Config.SolvedEmbeds.IngameSolvedReportEmbed.Thumbnail;
                            embedsBuilder.ImageUrl = string.IsNullOrEmpty(Config.SolvedEmbeds.IngameSolvedReportEmbed.Image) ? embedsBuilder.ImageUrl : Config.SolvedEmbeds.IngameSolvedReportEmbed.Image;
                            embedsBuilder.Color = Config.SolvedEmbeds.IngameSolvedReportEmbed.Color;
                            embedsBuilder.Footer = string.IsNullOrEmpty(Config.SolvedEmbeds.IngameSolvedReportEmbed.Footer) ? embedsBuilder.Footer : DiscordUtilities.ReplaceVariables(Config.SolvedEmbeds.IngameSolvedReportEmbed.Footer, replaceVariablesBuilder);
                            embedsBuilder.FooterTimestamp = Config.SolvedEmbeds.IngameSolvedReportEmbed.FooterTimestamp;

                            if (content != null)
                                content = string.IsNullOrEmpty(Config.SolvedEmbeds.IngameSolvedReportEmbed.Content) ? "" : DiscordUtilities.ReplaceVariables(Config.SolvedEmbeds.IngameSolvedReportEmbed.Content, replaceVariablesBuilder);
                        }
                        else
                        {
                            embedsBuilder.Title = string.IsNullOrEmpty(Config.SolvedEmbeds.ExpiredReportEmbed.Title) ? embedsBuilder.Title : DiscordUtilities.ReplaceVariables(Config.SolvedEmbeds.ExpiredReportEmbed.Title, replaceVariablesBuilder);
                            embedsBuilder.ThumbnailUrl = string.IsNullOrEmpty(Config.SolvedEmbeds.ExpiredReportEmbed.Thumbnail) ? embedsBuilder.ThumbnailUrl : Config.SolvedEmbeds.ExpiredReportEmbed.Thumbnail;
                            embedsBuilder.ImageUrl = string.IsNullOrEmpty(Config.SolvedEmbeds.ExpiredReportEmbed.Image) ? embedsBuilder.ImageUrl : Config.SolvedEmbeds.ExpiredReportEmbed.Image;
                            embedsBuilder.Color = Config.SolvedEmbeds.ExpiredReportEmbed.Color;
                            embedsBuilder.Footer = string.IsNullOrEmpty(Config.SolvedEmbeds.ExpiredReportEmbed.Footer) ? embedsBuilder.Footer : DiscordUtilities.ReplaceVariables(Config.SolvedEmbeds.ExpiredReportEmbed.Footer, replaceVariablesBuilder);
                            embedsBuilder.FooterTimestamp = Config.SolvedEmbeds.ExpiredReportEmbed.FooterTimestamp;

                            if (content != null)
                                content = string.IsNullOrEmpty(Config.SolvedEmbeds.ExpiredReportEmbed.Content) ? "" : DiscordUtilities.ReplaceVariables(Config.SolvedEmbeds.ExpiredReportEmbed.Content, replaceVariablesBuilder);
                        }
                    }
                    DiscordUtilities.UpdateMessage(message.MessageID, ulong.Parse(Config.ChannelID), content, embedsBuilder, null);
                }
            }
        }

        private void DiscordUtilitiesEventHandler(object? _, IDiscordUtilitiesEvent @event)
        {
            switch (@event)
            {
                case InteractionCreated interaction:
                    OnInteractionCreated(interaction.Interaction, interaction.User);
                    break;
                case CustomMessageReceived message:
                    OnCustomMessageReceived(message.CustomID, message.Message, message.isStored);
                    break;
                default:
                    break;
            }
        }
        private void OnCustomMessageReceived(string customID, MessageData message, bool isStored)
        {
            if (customID.Contains("report_"))
            {
                var reportId = customID.Replace("report_", "");
                if (!reportsList.ContainsKey(reportId))
                    return;

                var reportData = reportsList[reportId];
                reportData.messageId = message.MessageID;

                var replaceVariablesBuilder = new ReplaceVariables.Builder
                {
                    ServerData = true,
                    PlayerData = Utilities.GetPlayerFromSteamId(reportData.senderSteamId),
                    TargetData = Utilities.GetPlayerFromSteamId(reportData.targetSteamId),
                    CustomVariables = new Dictionary<string, string>{
                        { "{REASON}", reportData.reason }
                    },
                };

                var config = Config.ReportEmbed;
                var embedBuider = DiscordUtilities!.GetEmbedBuilderFromConfig(config, replaceVariablesBuilder);
                var content = DiscordUtilities.ReplaceVariables(Config.ReportEmbed.Content, replaceVariablesBuilder);

                var componentsBuilder = new Components.Builder();
                var InteractiveButtons = new List<Components.InteractiveButtonsBuilder>();
                if (Config.ReportEmbed.ReportButton.Enabled)
                {
                    InteractiveButtons.Add(
                    new Components.InteractiveButtonsBuilder
                    {
                        CustomId = $"report_{reportId}",
                        Label = Config.ReportEmbed.ReportButton.Text,
                        Color = (Components.ButtonColor)Config.ReportEmbed.ReportButton.Color,
                        Emoji = Config.ReportEmbed.ReportButton.Emoji,
                    }
                    );
                }
                var target = Utilities.GetPlayerFromSteamId(reportData.targetSteamId);
                if (target != null)
                {
                    if (Config.ReportEmbed.SearchPlayerButton.Enabled)
                    {
                        InteractiveButtons.Add(
                            new Components.InteractiveButtonsBuilder
                            {
                                CustomId = $"playerstatssearch_{Config.ReportEmbed.SearchPlayerButton.ServerName}:{target.SteamID}",
                                Label = Config.ReportEmbed.SearchPlayerButton.Text,
                                Color = (Components.ButtonColor)Config.ReportEmbed.SearchPlayerButton.Color,
                                Emoji = Config.ReportEmbed.SearchPlayerButton.Emoji,
                            }
                        );
                    }
                    if (Config.ReportEmbed.BanlistButton.Enabled)
                    {
                        InteractiveButtons.Add(
                            new Components.InteractiveButtonsBuilder
                            {
                                CustomId = $"banlist_report_{target.SteamID}",
                                Label = Config.ReportEmbed.BanlistButton.Text,
                                Color = (Components.ButtonColor)Config.ReportEmbed.BanlistButton.Color,
                                Emoji = Config.ReportEmbed.BanlistButton.Emoji,
                            }
                        );
                    }
                }
                componentsBuilder.InteractiveButtons = InteractiveButtons;

                DiscordUtilities.UpdateMessage(message.MessageID, message.ChannelID, content, embedBuider, componentsBuilder);
            }
        }
        private IDiscordUtilitiesAPI GetDiscordUtilitiesEventSender()
        {
            if (DiscordUtilities is not null)
            {
                return DiscordUtilities;
            }

            var DUApi = new PluginCapability<IDiscordUtilitiesAPI>("discord_utilities").Get();
            if (DUApi is null)
            {
                throw new Exception("Couldn't load Discord Utilities plugin");
            }

            DiscordUtilities = DUApi;
            return DUApi;
        }

        private string GetRandomChars()
        {
            int length = 15;

            string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
            var random = new Random();
            var keyBuilder = new StringBuilder(length);

            for (int i = 0; i < length; i++)
            {
                keyBuilder.Append(chars[random.Next(chars.Length)]);
            }

            return keyBuilder.ToString();
        }

        private static string ExtractChatMessage(CommandInfo message)
        {
            string commandString = message.GetCommandString;
            if (!string.IsNullOrWhiteSpace(commandString))
            {
                int firstSpace = commandString.IndexOf(' ');
                if (firstSpace >= 0 && firstSpace < commandString.Length - 1)
                {
                    return commandString[(firstSpace + 1)..].Trim().Trim('"');
                }
            }

            string argString = message.ArgString?.Trim().Trim('"').Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(argString))
            {
                return argString;
            }

            return message.GetArg(1).Trim();
        }

        private bool IsCancelReasonInput(string reason)
        {
            var cancelCommand = Config.CancelCommand.Trim();
            if (string.IsNullOrWhiteSpace(cancelCommand))
                return false;

            reason = reason.Trim();
            return reason.Equals(cancelCommand, StringComparison.OrdinalIgnoreCase)
                || reason.Equals($"!{cancelCommand}", StringComparison.OrdinalIgnoreCase);
        }

        private void StartPendingReport(CCSPlayerController sender, CCSPlayerController target)
        {
            CancelPendingReport(sender.SteamID, notifyPlayer: false);
            pendingReports[sender.SteamID] = new PendingReportState
            {
                TargetSteamId = target.SteamID,
                TimeoutTimer = CreatePendingReportTimer(sender.SteamID)
            };
        }

        private CssTimer CreatePendingReportTimer(ulong senderSteamId)
        {
            return AddTimer(PendingReportTimeoutSeconds, () => ExpirePendingReport(senderSteamId), TimerFlags.STOP_ON_MAPCHANGE);
        }

        private void ResetPendingReportTimeout(ulong senderSteamId)
        {
            if (!pendingReports.TryGetValue(senderSteamId, out var pendingReport))
                return;

            pendingReport.TimeoutTimer?.Kill();
            pendingReport.TimeoutTimer = CreatePendingReportTimer(senderSteamId);
        }

        private void ExpirePendingReport(ulong senderSteamId)
        {
            if (!pendingReports.Remove(senderSteamId, out _))
                return;

            NotifyPendingReportCancelled(senderSteamId, "Chat.ReportCancelledTimeout");
        }

        private void CancelPendingReport(ulong senderSteamId, bool notifyPlayer)
        {
            if (!pendingReports.Remove(senderSteamId, out var pendingReport))
                return;

            pendingReport.TimeoutTimer?.Kill();
            if (notifyPlayer)
                NotifyPendingReportCancelled(senderSteamId, "Chat.ReportCancelled");
        }

        private void ClearPendingReports(bool notifyPlayers)
        {
            foreach (var senderSteamId in pendingReports.Keys.ToList())
            {
                CancelPendingReport(senderSteamId, notifyPlayers);
            }
        }

        private void NotifyPendingReportCancelled(ulong senderSteamId, string localizerKey)
        {
            var sender = Utilities.GetPlayerFromSteamId(senderSteamId);
            if (sender != null && sender.IsValid)
                sender.PrintToChat($"{Localizer["Chat.Prefix"]} {Localizer[localizerKey]}");
        }
    }
}