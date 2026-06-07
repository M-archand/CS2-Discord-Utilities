using System.Collections.Concurrent;
using Discord;
using Discord.WebSocket;

namespace DiscordUtilities
{
    public partial class DiscordUtilities
    {
        // Accessed from both the Discord.NET thread-pool and the CS2 main thread; ConcurrentDictionary keeps structural operations atomic.
        public static ConcurrentDictionary<int, SocketInteraction> savedInteractions = new();
        
        // Insert time (UTC) per saved interaction, used to cleanup stale entries
        public static ConcurrentDictionary<int, DateTime> savedInteractionsAdded = new();

        public static void AddSavedInteraction(int interactionId, SocketInteraction interaction)
        {
            savedInteractions[interactionId] = interaction;
            savedInteractionsAdded[interactionId] = DateTime.UtcNow;
        }

        public static void RemoveSavedInteraction(int interactionId)
        {
            savedInteractions.TryRemove(interactionId, out _);
            savedInteractionsAdded.TryRemove(interactionId, out _);
        }

        public static void ClearSavedInteractions()
        {
            savedInteractions.Clear();
            savedInteractionsAdded.Clear();
        }
        public static ConcurrentDictionary<ulong, IUserMessage> savedMessages = new();
        public static ConcurrentDictionary<int, PlayerData> playerData = new();
        public static ConcurrentDictionary<ulong, ulong> linkedPlayers = new();

        // Reverse index (discordId -> steamId) kept in sync with linkedPlayers so
        // Discord-user lookups are O(1) instead of O(n) ContainsValue/FirstOrDefault scans.
        public static ConcurrentDictionary<ulong, ulong> linkedPlayersReverse = new();

        public static void AddLinkedPlayer(ulong steamId, ulong discordId)
        {
            linkedPlayers[steamId] = discordId;
            linkedPlayersReverse[discordId] = steamId;
        }

        public static bool RemoveLinkedPlayer(ulong steamId)
        {
            if (linkedPlayers.TryRemove(steamId, out var discordId))
            {
                linkedPlayersReverse.TryRemove(discordId, out _);
                return true;
            }
            return false;
        }

        public static void ClearLinkedPlayers()
        {
            linkedPlayers.Clear();
            linkedPlayersReverse.Clear();
        }
        public static ConcurrentDictionary<string, string> linkCodes = new();
        public static Dictionary<string, List<ConditionData>> customConditions = new();
        public static Dictionary<string, replaceDataType> customVariables = new();
        public static List<string> mapImagesList = new();
        public static Dictionary<string, string> moduleVersions = new();
    }
}