using Discord;
using Discord.WebSocket;

namespace DiscordUtilities
{
    public partial class DiscordUtilities
    {
        public static Dictionary<int, SocketInteraction> savedInteractions = new();
        public static Dictionary<ulong, IUserMessage> savedMessages = new();
        public static Dictionary<int, PlayerData> playerData = new();
        public static Dictionary<ulong, ulong> linkedPlayers = new();
        
        // Reverse index (discordId -> steamId) kept in sync with linkedPlayers so
        // Discord-user lookups are O(1) instead of O(n) ContainsValue/FirstOrDefault scans.
        public static Dictionary<ulong, ulong> linkedPlayersReverse = new();

        public static void AddLinkedPlayer(ulong steamId, ulong discordId)
        {
            linkedPlayers[steamId] = discordId;
            linkedPlayersReverse[discordId] = steamId;
        }

        public static bool RemoveLinkedPlayer(ulong steamId)
        {
            if (linkedPlayers.TryGetValue(steamId, out var discordId))
            {
                linkedPlayers.Remove(steamId);
                linkedPlayersReverse.Remove(discordId);
                return true;
            }
            return false;
        }

        public static void ClearLinkedPlayers()
        {
            linkedPlayers.Clear();
            linkedPlayersReverse.Clear();
        }
        public static Dictionary<string, string> linkCodes = new();
        public static Dictionary<string, List<ConditionData>> customConditions = new();
        public static Dictionary<string, replaceDataType> customVariables = new();
        public static List<string> mapImagesList = new();
        public static Dictionary<string, string> moduleVersions = new();
    }
}