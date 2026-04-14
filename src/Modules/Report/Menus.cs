using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CS2MenuManager.API.Enum;
using CS2MenuManager.API.Menu;

namespace Report
{
    public partial class Report
    {
        public void OpenReportsList_Menu(CCSPlayerController player)
        {
            if (reportsList.Count == 0)
            {
                player.PrintToChat($"{Localizer["Chat.Prefix"]} {Localizer["Chat.NoReportsFound"]}");
                return;
            }
            WasdMenu Menu = new(Localizer["Menu.ReportsList", reportsList.Count], this);

            foreach (var item in reportsList)
            {
                var data = item.Value;
                if (Config.ReportMethod != 3)
                    Menu.AddItem(Localizer["Menu.ReportInfo", data.targetName, data.reason], (player, option) => OpenReportData_Menu(player, item.Key));
                else
                    Menu.AddItem(Localizer["Menu.ReportInfo", data.senderName, data.reason], (player, option) => OpenReportData_Menu(player, item.Key));
            }

            Menu.Display(player, 0);
        }

        public void OpenReportData_Menu(CCSPlayerController player, string reportId)
        {
            var data = reportsList[reportId];
            WasdMenu Menu = new(Localizer["Menu.ReportDetails"], this);

            Menu.AddItem(Localizer["Menu.MarkAsSolved"], (player, option) => ReportSolved(player, reportId));
            Menu.AddItem(Localizer["Menu.ReportInfo.Sender", data.senderName], DisableOption.DisableHideNumber);
            Menu.AddItem(Localizer["Menu.ReportInfo.Reason", data.reason], DisableOption.DisableHideNumber);

            if (Config.ReportMethod != 3)
                Menu.AddItem(Localizer["Menu.ReportInfo.Target", data.targetName], DisableOption.DisableHideNumber);

            Menu.AddItem(Localizer["Menu.ReportInfo.Time", data.time.ToString(Config.DateFormat)], DisableOption.DisableHideNumber);

            Menu.Display(player, 0);
        }

        public void ReportSolved(CCSPlayerController player, string reportId)
        {
            player.PrintToChat($"{Localizer["Chat.Prefix"]} {Localizer["Chat.ReportSolved"]}");
            if (reportsList.ContainsKey(reportId))
            {
                PerformReportSolved(reportId, 0, player);
            }
        }

        public void OpenReportMenu_Players(CCSPlayerController player)
        {
            if (!Config.SelfReport)
            {
                if (GetTargetsForReportCount(player) == 0)
                {
                    player.PrintToChat($"{Localizer["Chat.Prefix"]} {Localizer["Chat.ReportNoTargetsFound"]}");
                    return;
                }
            }

            WasdMenu Menu = new(Localizer["Menu.ReportSelectPlayer"], this);

            if (Config.SelfReport)
            {
                foreach (var p in Utilities.GetPlayers().Where(p => p.IsValid && p.SteamID.ToString().Length == 17))
                    Menu.AddItem(p.PlayerName, (player, option) => OnSelectPlayer_ReportMenu(player, p));
            }
            else
            {
                foreach (var p in Utilities.GetPlayers().Where(p => p != null && p.IsValid && p != player && DiscordUtilities!.IsPlayerDataLoaded(p) && p.Connected == PlayerConnectedState.PlayerConnected && p.SteamID.ToString().Length == 17 && !AdminManager.PlayerHasPermissions(p, Config.UnreportableFlag)))
                    Menu.AddItem(p.PlayerName, (player, option) => OnSelectPlayer_ReportMenu(player, p));
            }

            Menu.Display(player, 0);
        }

        public void OpenReportMenu_Reason(CCSPlayerController player, CCSPlayerController target)
        {
            var selectedTarget = target;
            string[] Reasons = Config.ReportReasons.Split(',');
            WasdMenu Menu = new(Localizer["Menu.ReportSelectReason"], this);
            foreach (var reason in Reasons)
            {
                if (reason.Contains("#CUSTOMREASON"))
                    Menu.AddItem(Localizer["Menu.ReportCustomReason"], (player, option) => CustomReasonReport(player, selectedTarget));
                else
                    Menu.AddItem(reason, (player, option) => SendReport(player, selectedTarget, reason));
            }

            Menu.Display(player, 0);
        }

        private void OnSelectPlayer_ReportMenu(CCSPlayerController player, CCSPlayerController target)
        {
            if (Config.AntiSpamReport && solvedPlayers.Contains(target.SteamID))
            {
                player.PrintToChat($"{Localizer["Chat.Prefix"]} {Localizer["Chat.ThisPlayerCannotBeReported", target.PlayerName]}");
                return;
            }
            CustomReasonReport(player, target);
        }
    }
}