﻿using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using Discord;
using Discord.Commands;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using CounterStrikeSharp.API.Core.Capabilities;
using DiscordUtilitiesAPI.Events;

namespace DiscordUtilities
{
    public partial class DiscordUtilities : BasePlugin, IPluginConfig<DUConfig>
    {
        public override string ModuleName => "Discord Utilities";
        public override string ModuleAuthor => "Nocky, Marchand";
        public override string ModuleVersion => "2.3.0";
        private bool mapStarted;
        private Listeners.OnMapStart? onMapStartListener;
        private Listeners.OnMapEnd? onMapEndListener;
        private int playedTimeFlushCounter;
        public void OnConfigParsed(DUConfig config)
        {
            Config = config;
        }
        public override void OnAllPluginsLoaded(bool hotReload)
        {
            if (!string.IsNullOrEmpty(Config.Database.Password) && !string.IsNullOrEmpty(Config.Database.Host) && !string.IsNullOrEmpty(Config.Database.DatabaseName) && !string.IsNullOrEmpty(Config.Database.User))
            {
                databaseData = new DatabaseConnection
                {
                    Server = Config.Database.Host,
                    Port = (uint)Config.Database.Port,
                    User = Config.Database.User,
                    Database = Config.Database.DatabaseName,
                    Password = Config.Database.Password,
                };

                _ = InitializeServices();
            }
            else
            {
                Perform_SendConsoleMessage("You need to setup Database credentials in config", ConsoleColor.Red);
            }
        }

        private async Task InitializeServices()
        {
            await CreateDatabaseConnection();
            _ = LoadDiscordBOT();

            if (string.IsNullOrEmpty(Config.ServerID) || !ulong.TryParse(Config.ServerID, out _))
            {
                Perform_SendConsoleMessage("Invalid Discord Server ID!", ConsoleColor.Red);
                return;
            }

            // After a grace period, warn if the bot still hasn't reached the gateway-ready state.
            await Task.Delay(15000);
            if (!IsBotConnected)
                Perform_SendConsoleMessage("Discord BOT failed to connect!", ConsoleColor.Red);
        }
        public override void Load(bool hotReload)
        {
            Capabilities.RegisterPluginCapability(DiscordUtilitiesAPI, () => this);

            interactionCleanupTimer?.Kill();
            interactionCleanupTimer = AddTimer(30.0f, CleanupSavedInteractions, TimerFlags.REPEAT);

            CreateCustomCommands();
            if (Config.UseCustomVariables)
                LoadCustomConditions();

            _ = LoadVersions();
            _ = LoadMapImages();
            IsDbConnected = false;
            IsBotConnected = false;
            IsDebug = Config.Debug;
            ServerId = Config.ServerID;
            UseCustomVariables = Config.UseCustomVariables;
            DateFormat = Config.DateFormat;
            ClearSavedInteractions();

            serverData.ModuleDirectory = ModuleDirectory;
            serverData.IP = Config.ServerIP;

            Server.ExecuteCommand("sv_hibernate_when_empty false");
            mapStarted = false;
            onMapStartListener = mapName =>
            {
                if (!mapStarted)
                {
                    mapStarted = true;
                    Server.ExecuteCommand("sv_hibernate_when_empty false");
                    playerData.Clear();
                    AddTimer(4.0f, () =>
                    {
                        if (Config.TimedRoles)
                            CheckExpiredTimedRoles();

                        UpdateServerData();
                        serverData.MapName = mapName;
                        serverData.GameDirectory = Server.GameDirectory;
                        serverData.MaxPlayers = Server.MaxPlayers.ToString();
                        ServerDataLoaded();
                    });

                    updateTimer?.Kill();
                    playedTimeFlushCounter = 0;
                    updateTimer = AddTimer(60.0f, () =>
                    {
                        UpdateServerData();
                        foreach (var player in Utilities.GetPlayers().Where(p => !p.IsBot && !p.IsHLTV && p.Connected == PlayerConnectedState.Connected && p.AuthorizedSteamID != null && playerData.ContainsKey(p.Slot)))
                        {
                            playerData[player.Slot].PlayedTime++;
                        }
                        playedTimeFlushCounter++;
                        if (playedTimeFlushCounter >= 5)
                        {
                            playedTimeFlushCounter = 0;
                            FlushPlayedTime();
                        }
                    }, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
                }
            };
            onMapEndListener = () =>
            {
                mapStarted = false;
                FlushPlayedTime();
            };
            RegisterListener(onMapStartListener);
            RegisterListener(onMapEndListener);
        }

        private async Task LoadDiscordBOT()
        {
            try
            {
                BotCancellation = new CancellationTokenSource();
                BotClient = new DiscordSocketClient(new DiscordSocketConfig()
                {
                    AlwaysDownloadUsers = true,
                    UseInteractionSnowflakeDate = false,
                    GatewayIntents = GatewayIntents.MessageContent | GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.GuildMembers | GatewayIntents.GuildScheduledEvents
                });

                BotCommands = new CommandService();
                BotServices = new ServiceCollection()
                    .AddSingleton(BotClient)
                    .AddSingleton(BotCommands)
                    .BuildServiceProvider();

                await BotClient.LoginAsync(TokenType.Bot, Config.Token);
                await BotClient.StartAsync();

                BotClient.Ready += ReadyAsync;

                await Task.Delay(Timeout.Infinite, BotCancellation.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected on Unload/hot-reload
            }
            catch (Exception ex)
            {
                Perform_SendConsoleMessage($"An error occurred while initializing the Discord BOT: '{ex.Message}'", ConsoleColor.Red);
            }
        }
        private async Task ReadyAsync()
        {
            Perform_SendConsoleMessage("Discord BOT has been connected!", ConsoleColor.Green);
            IsBotConnected = true;
            BotLoaded();

            if (string.IsNullOrEmpty(ServerId))
                Perform_SendConsoleMessage("You do not have a completed 'Server ID'!", ConsoleColor.Red);
            else
            {
                var guild = BotClient!.GetGuild(ulong.Parse(ServerId));
                if (guild == null)
                    Perform_SendConsoleMessage($"Guild with id '{ServerId}' was not found!", ConsoleColor.Red);
            }

            var ActivityFormat = ReplaceServerDataVariables(Config.BotStatus.ActivityFormat);
            await BotClient!.SetActivityAsync(new Game(ActivityFormat, (ActivityType)Config.BotStatus.ActivityType, ActivityProperties.None));
            await BotClient.SetStatusAsync((UserStatus)Config.BotStatus.Status);

            BotClient.SlashCommandExecuted += SlashCommandHandler;
            BotClient.MessageReceived += MessageReceivedHandler;
            BotClient.InteractionCreated += InteractionCreatedHandler;
            BotClient.GuildScheduledEventCreated += ScheduledEventCreated;
            BotClient.GuildMemberUpdated += GuildMemberUpdated;
            BotClient.UserLeft += GuildMemberLeft;

            var linkCommand = new SlashCommandBuilder()
                .WithName(Config.Link.LinkDiscordSettings.Name.ToLower())
                .WithDescription(Config.Link.LinkDiscordSettings.Description)
                .AddOption(Config.Link.LinkDiscordSettings.OptionName.ToLower(), ApplicationCommandOptionType.String, Config.Link.LinkDiscordSettings.OptionDescription, isRequired: true);

            try
            {
                if (Config.Link.Enabled)
                {
                    if (IsDbConnected)
                    {
                        if (IsDebug)
                            Perform_SendConsoleMessage($"Link Slash Command has been successfully updated/created", ConsoleColor.Cyan);
                        await BotClient.CreateGlobalApplicationCommandAsync(linkCommand.Build());
                    }
                    else
                    {
                        Perform_SendConsoleMessage($"Link Slash Command was not created because you do not have a database connected", ConsoleColor.Red);
                        throw new Exception("Link Slash Command was not created because you do not have a database connected");
                    }
                }
            }
            catch (Exception ex)
            {
                Perform_SendConsoleMessage($"An error occurred while updating Link Slash Commands: '{ex.Message}'", ConsoleColor.Red);
                throw new Exception($"An error occurred while updating Link Slash Commands: {ex.Message}");
            }
        }

        private async Task GuildMemberLeft(SocketGuild guild, SocketUser user)
        {
            if (Config.Link.Enabled && Config.Link.ResponseServer)
            {
                if (linkedPlayersReverse.TryGetValue(user.Id, out var steamId))
                {
                    await RemovePlayerData(steamId.ToString());
                    await CreateScheduledEventAsync($"removelinked;{steamId}");
                }
            }
        }

        private async Task GuildMemberUpdated(Cacheable<SocketGuildUser, ulong> before, SocketGuildUser after)
        {
            if (!linkedPlayersReverse.ContainsKey(after.Id))
                return;

            var beforeGuildUser = await before.GetOrDownloadAsync();

            var beforeRoles = beforeGuildUser.Roles.Select(r => r.Id).ToList();
            var afterRoles = after.Roles.Select(r => r.Id).ToList();

            var addedRoles = afterRoles
                .Except(beforeRoles)
                .Select(role => role.ToString())
                .ToList();

            var removedRoles = beforeRoles
                .Except(afterRoles)
                .Select(role => role.ToString())
                .ToList();

            Server.NextFrame(() =>
            {
                var user = GetUserDataByUserID(after.Id);
                if (user == null)
                    return;

                DiscordUtilitiesAPI.Get()?.TriggerEvent(new LinkedUserRolesUpdated(user, addedRoles, removedRoles));
                if (IsDebug)
                    Perform_SendConsoleMessage("New Event Triggered: 'LinkedUserRolesUpdated'", ConsoleColor.Cyan);
            });
        }

        public async Task CreateScheduledEventAsync(string eventData)
        {
            var guild = BotClient!.GetGuild(ulong.Parse(ServerId));
            if (guild == null)
                return;

            var guildEvent = await guild.CreateEventAsync($"Custom Event (DU)", DateTimeOffset.UtcNow.AddMinutes(1), GuildScheduledEventType.External, endTime: DateTimeOffset.UtcNow.AddMinutes(2), location: "Discord Utilities", description: eventData);
            await guildEvent.DeleteAsync();
        }

        private Task ScheduledEventCreated(SocketGuildEvent scheduledEvent)
        {
            if (scheduledEvent.Location.Equals("Discord Utilities"))
            {
                if (scheduledEvent.Name.Contains("Custom Event (DU)"))
                {
                    var data = scheduledEvent.Description.Split(';');
                    var CustomId = data.FirstOrDefault();
                    if (CustomId == null)
                        return Task.CompletedTask;

                    if (CustomId.Equals("addcode"))
                    {
                        var code = DecodeSecretString(data[1]);
                        if (!linkCodes.ContainsKey(code))
                            linkCodes[code] = data[2];
                    }
                    else if (CustomId.Equals("removecode"))
                    {
                        var code = DecodeSecretString(data[1]);
                        linkCodes.TryRemove(code, out _);
                    }
                    else if (CustomId.Equals("addlinked"))
                    {
                        if (data.Length >= 3 && ulong.TryParse(data[1], out var steamid) && ulong.TryParse(data[2], out var discordid))
                        {
                            if (!linkedPlayers.ContainsKey(steamid))
                                AddLinkedPlayer(steamid, discordid);
                        }
                    }
                    else if (CustomId.Equals("removelinked"))
                    {
                        if (data.Length >= 2 && ulong.TryParse(data[1], out var steamid))
                        {
                            RemoveLinkedPlayer(steamid);
                        }
                    }
                }
            }
            return Task.CompletedTask;
        }

        private Task InteractionCreatedHandler(SocketInteraction interaction)
        {
            if (interaction is SocketMessageComponent MessageComponent)
            {
                Event_InteractionCreated(interaction, MessageComponent);
            }
            else if (interaction.Type == InteractionType.ModalSubmit)
            {
                Event_ModalSubmited(interaction);
            }
            return Task.CompletedTask;
        }

        private Task MessageReceivedHandler(SocketMessage message)
        {
            if (message.Author.IsBot || message.Author.IsWebhook)
                return Task.CompletedTask;

            Event_MessageReceived(message);
            return Task.CompletedTask;
        }

        private async Task SlashCommandHandler(SocketSlashCommand command)
        {
            if (command.CommandName == Config.Link.LinkDiscordSettings.Name.ToLower())
                await DiscordLink_CMD(command);
            else
                Event_SlashCommand(command);
        }

        // Cleanup old interactions
        private void CleanupSavedInteractions()
        {
            var cutoff = DateTime.UtcNow.AddSeconds(-60);
            foreach (var entry in savedInteractionsAdded)
            {
                if (entry.Value < cutoff)
                    RemoveSavedInteraction(entry.Key);
            }
        }

        private void FlushPlayedTime()
        {
            if (!IsDbConnected)
                return;

            var playedTimes = playerData.Values
                .Where(p => p.PlayedTime > 0)
                .Select(p => new KeyValuePair<string, int>(p.SteamId64, p.PlayedTime))
                .ToList();

            if (playedTimes.Count > 0)
                _ = SavePlayedTimeData(playedTimes);
        }

        public override void Unload(bool hotReload)
        {
            FlushPlayedTime();

            if (updateTimer != null)
            {
                updateTimer.Kill();
                updateTimer = null;
            }

            if (interactionCleanupTimer != null)
            {
                interactionCleanupTimer.Kill();
                interactionCleanupTimer = null;
            }

            if (onMapStartListener != null)
            {
                RemoveListener(onMapStartListener);
                onMapStartListener = null;
            }
            if (onMapEndListener != null)
            {
                RemoveListener(onMapEndListener);
                onMapEndListener = null;
            }
            mapStarted = false;

            if (BotCancellation != null)
            {
                BotCancellation.Cancel();
                BotCancellation.Dispose();
                BotCancellation = null;
            }

            var client = BotClient;
            if (client != null)
            {
                client.Ready -= ReadyAsync;
                client.SlashCommandExecuted -= SlashCommandHandler;
                client.MessageReceived -= MessageReceivedHandler;
                client.InteractionCreated -= InteractionCreatedHandler;
                client.GuildScheduledEventCreated -= ScheduledEventCreated;
                client.GuildMemberUpdated -= GuildMemberUpdated;
                client.UserLeft -= GuildMemberLeft;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await client.StopAsync();
                        await client.LogoutAsync();
                    }
                    catch (Exception ex)
                    {
                        Perform_SendConsoleMessage($"An error occurred while shutting down the Discord BOT: '{ex.Message}'", ConsoleColor.Red);
                    }
                    finally
                    {
                        client.Dispose();
                    }
                });
            }

            BotClient = null;
            IsBotConnected = false;
        }

        public static void Perform_SendConsoleMessage(string text, ConsoleColor color)
        {
            string prefix = "[Discord Utilities] ";
            string suffix = text;

            switch (color)
            {
                case ConsoleColor.Cyan:
                    prefix = "[Discord Utilities] (DEBUG): ";
                    break;
                case ConsoleColor.Red:
                    prefix = "[Discord Utilities] (ERROR): ";
                    break;
            }

            Console.ForegroundColor = color;
            Console.Write(prefix);

            Console.ForegroundColor = ConsoleColor.White;
            bool isInQuotes = false;

            foreach (char c in suffix)
            {
                if (c == '\'')
                {
                    isInQuotes = !isInQuotes;
                    continue;
                }
                Console.ForegroundColor = isInQuotes ? ConsoleColor.Yellow : ConsoleColor.White;
                Console.Write(c);
            }

            Console.WriteLine();
            Console.ResetColor();
        }
    }
}
