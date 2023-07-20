﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using JKClient;
using Client = JKClient.JKClient;

namespace JKWatcher
{

    enum ConfigStringDefines
    {
        //CS_MUSIC = 2,
        //CS_MESSAGE = 3,     // from the map worldspawn's message field
        //CS_MOTD = 4,        // g_motd string for server message of the day
        //CS_WARMUP = 5,      // server time when the match will be restarted
        //CS_SCORES1 = 6,
        //CS_SCORES2 = 7,
        //CS_VOTE_TIME = 8,
        //CS_VOTE_STRING = 9,
        //CS_VOTE_YES = 10,
        //CS_VOTE_NO = 11,

        //CS_TEAMVOTE_TIME = 12,
        //CS_TEAMVOTE_STRING = 14,

        //CS_TEAMVOTE_YES = 16,
        //CS_TEAMVOTE_NO = 18,

        //CS_GAME_VERSION = 20,
        //CS_LEVEL_START_TIME = 21,       // so the timer only shows the current level
        //CS_INTERMISSION = 22,       // when 1, fraglimit/timelimit has been hit and intermission will start in a second or two
        CS_FLAGSTATUS = 23,     // string indicating flag status in CTF
        //CS_SHADERSTATE = 24,
        //CS_BOTINFO = 25,

        //CS_MVSDK = 26,      // CS for mvsdk specific configuration

        //CS_ITEMS = 27,      // string of 0's and 1's that tell which items are present

        //CS_CLIENT_JEDIMASTER = 28,      // current jedi master
        //CS_CLIENT_DUELWINNER = 29,      // current duel round winner - needed for printing at top of scoreboard
        //CS_CLIENT_DUELISTS = 30,        // client numbers for both current duelists. Needed for a number of client-side things.

        // these are also in be_aas_def.h - argh (rjr)
        //CS_MODELS=32
    }

    enum PersistantEnum {
        PERS_SCORE=0,                     // !!! MUST NOT CHANGE, SERVER AND GAME BOTH REFERENCE !!!
        //PERS_HITS=1,                      // total points damage inflicted so damage beeps can sound on change
        //PERS_RANK=2,                      // player rank or team rank
        //PERS_TEAM=3,                      // player team
        //PERS_SPAWN_COUNT=4,               // incremented every respawn
        //PERS_PLAYEREVENTS=5,              // 16 bits that can be flipped for events
        //PERS_ATTACKER=6,                  // clientnum of last damage inflicter
        //PERS_ATTACKEE_ARMOR=7,            // health/armor of last person we attacked
        //PERS_KILLED=8,                    // count of the number of times you died
                                        // player awards tracking
        //PERS_IMPRESSIVE_COUNT=9,          // two railgun hits in a row
        //PERS_EXCELLENT_COUNT=10,           // two successive kills in a short amount of time
        //PERS_DEFEND_COUNT=11,              // defend awards
        //PERS_ASSIST_COUNT=12,              // assist awards
        //PERS_GAUNTLET_FRAG_COUNT=13,       // kills with the guantlet
        //PERS_CAPTURES=14                   // captures
    }

    public enum RequestCategory
    {
        NONE,
        SCOREBOARD,
        FOLLOW,
        INFOCOMMANDS,
        MEME,
        KILLTRACKER,
        SELFKILL,
        GOINTOSPEC,
        GOINTOSPECHACK,
        FIGHTBOT,
        FIGHTBOT_QUEUED,
        BOTSAY
    }

    struct MvHttpDownloadInfo
    {
        public bool httpIsAvailable;
        public string urlPrefix;
    }

    public class SnapStatusInfo {
        const int averageWindow = 30; // Max samples
        const int averageWindowTime = 2000; // Max time window

        private int lastServerTime = 0;
        private int lastMessageNum = 0;
        private struct SnapStatusInfoSnippet
        {
            public int duration; // difference from last serverTime
            public int serverTime;
            public int snapNumIncrementSinceLast; // (skipped packets+1 basically)
        }

        private SnapStatusInfoSnippet[] infoSnippets = new SnapStatusInfoSnippet[averageWindow];
        private int index = 0;

        public void addDataPoint(int messageNum, int serverTime)
        {
            infoSnippets[index].duration = serverTime - lastServerTime;
            infoSnippets[index].snapNumIncrementSinceLast = messageNum - lastMessageNum;
            infoSnippets[index].serverTime = serverTime;
            lastServerTime = serverTime;
            lastMessageNum = messageNum;
            index = (index + 1) % averageWindow;
        }

        public int ReceivedSnaps { get; private set; } = 0;
        public int TotalSnaps { get; private set; } = 0;

        public override string ToString()
        {
            int timeTotal = 0;
            int packetCount = 0;
            int packetCountIncludingSkipped = 0;
            foreach(SnapStatusInfoSnippet snippet in infoSnippets)
            {
                if((lastServerTime- snippet.serverTime) < averageWindowTime)
                {
                    packetCount++;
                    packetCountIncludingSkipped += snippet.snapNumIncrementSinceLast;
                    timeTotal += snippet.duration;
                }
            }
            ReceivedSnaps = timeTotal == 0 ? 0 : 1000 * packetCount / timeTotal;
            TotalSnaps = timeTotal == 0 ? 0 : 1000 * packetCountIncludingSkipped / timeTotal;
            return $"{ReceivedSnaps}/{TotalSnaps}";
        }
    }

    /*
     * General notes regarding flood protection.
     * Old style servers only allow 1 command per second period. If you send a command within a second of another command,
     * it's game over. Newer ones apparently allow bursts of 3 commands, and then the 1 second limit is enforced again.
     * This tool more or less hopes for the latter. It would be hard to control command timing to the degree necessary
     * to avoid ever having less than 1 second delay between two commands.
     * 
     * We try to generally stay within the limits though naturally. Experience will show how well it will work.
     * 
     */
    public partial class Connection : INotifyPropertyChanged
    {
        // Setting it a bit higher than in the jk2 code itself, just to be safe. Internet delays etc. could cause issues.
        // Still not absolutely foolproof I guess but about as good as I can do.
        const int floodProtectPeriod = 1100; 

        public Client client;
        private ConnectedServerWindow serverWindow;

        public event PropertyChangedEventHandler PropertyChanged;

        public event UserCommandGeneratedEventHandler ClientUserCommandGenerated;
        public event Action<ServerInfo> ServerInfoChanged; // forward to the outside if desired
        internal void OnClientUserCommandGenerated(ref UserCommand cmd, in UserCommand previousCommand)
        {
            this.ClientUserCommandGenerated?.Invoke(this, ref cmd, in previousCommand);
        }

        public JKClient.Statistics clientStatistics { get; private set; }
        public bool GhostPeer { get; private set; } = false;

        // To detect changes.
        private string lastKnownPakNames = "";
        private string lastKnownPakChecksums = "";

        public int? ClientNum { get; set; } = null;
        public int? SpectatedPlayer { get; set; } = null;
        public PlayerMoveType? PlayerMoveType { get; set; } = null;
        public int? Index { get; set; } = null;
        public int? CameraOperator { get; set; } = null;

        private bool trulyDisconnected = true; // If we disconnected manually we want to stay disconnected.

        public SnapStatusInfo SnapStatus { get; private set; } = new SnapStatusInfo();

        public bool AlwaysFollowSomeone { get; set; } = true;

        //public ConnectionStatus Status => client != null ? client.Status : ConnectionStatus.Disconnected;
        public ConnectionStatus Status { get; private set; } = ConnectionStatus.Disconnected;

        private ServerSharedInformationPool infoPool;

        public string GameTime { get; set; } = null;

        public bool isRecordingADemo { get; private set; } = false;

        public LeakyBucketRequester<string, RequestCategory> leakyBucketRequester = null;

        MvHttpDownloadInfo? mvHttpDownloadInfo = null;


        private List<CancellationTokenSource> backgroundTasks = new List<CancellationTokenSource>();


        /*public Connection(ConnectedServerWindow serverWindowA, string ip, ProtocolVersion protocol, ServerSharedInformationPool infoPoolA)
        {
            infoPool = infoPoolA;
            serverWindow = serverWindowA;
            _ = createConnection(ip, protocol);
        }
        public Connection(ServerInfo serverInfo, ConnectedServerWindow serverWindowA,ServerSharedInformationPool infoPoolA)
        {
            infoPool = infoPoolA;
            serverWindow = serverWindowA;
            leakyBucketRequester = new LeakyBucketRequester<string, RequestCategory>(3, floodProtectPeriod); // Assuming default sv_floodcontrol 3, but will be adjusted once known
            leakyBucketRequester.CommandExecuting += LeakyBucketRequester_CommandExecuting; ;
            _ = createConnection(serverInfo.Address.ToString(), serverInfo.Protocol);
            createPeriodicReconnecter();
        }*/

        private string password = null;
        //private string userInfoName = null;
        //private bool demoTimeNameColors = false;
        //private bool attachClientNumToName = false;

        SnapsSettings snapsSettings = null;

        ConnectedServerWindow.ConnectionOptions _connectionOptions = null;

        private bool jkaMode = false;
        private bool JAPlusDetected = false;
        private bool JAProDetected = false;
        private bool MBIIDetected = false;

        public Connection( NetAddress addressA, ProtocolVersion protocolA, ConnectedServerWindow serverWindowA, ServerSharedInformationPool infoPoolA, ConnectedServerWindow.ConnectionOptions connectionOptions, string passwordA = null, /*string userInfoNameA = null, bool dateTimeColorNamesA = false, bool attachClientNumToNameA = false,*/ SnapsSettings snapsSettingsA = null, bool ghostPeer = false)
        {
            if(connectionOptions == null)
            {
                throw new InvalidOperationException("Cannot create connection with null connectionOptions");
            }
            this.GhostPeer = ghostPeer;
            _connectionOptions = connectionOptions;
            _connectionOptions.PropertyChanged += _connectionOptions_PropertyChanged;
            if (protocolA == ProtocolVersion.Protocol26)
            {
                jkaMode = true;
            }
            snapsSettings = snapsSettingsA;
            //demoTimeNameColors = dateTimeColorNamesA;
            //attachClientNumToName = attachClientNumToNameA;
            infoPool = infoPoolA;
            serverWindow = serverWindowA;
            //userInfoName = userInfoNameA;
            password = passwordA;
            leakyBucketRequester = new LeakyBucketRequester<string, RequestCategory>(3, floodProtectPeriod); // Assuming default sv_floodcontrol 3, but will be adjusted once known
            leakyBucketRequester.CommandExecuting += LeakyBucketRequester_CommandExecuting; ;
            _ = createConnection(addressA.ToString(), protocolA);
            createPeriodicReconnecter();
        }

        private void _connectionOptions_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if(e.PropertyName == "demoTimeColorNames" || e.PropertyName == "userInfoName" || e.PropertyName == "attachClientNumToName")
            {
                updateName();
            } else if(e.PropertyName == "skin")
            {
                updateSkin();
            }
        }

        public void SetPassword(string passwordA)
        {
            password = passwordA;
            if(client != null)
            {
                client.Password = password != null ? password : "";
            }
        }
        /*
        public void SetUserInfoName(string userInfoNameA)
        {
            userInfoName = userInfoNameA;
            updateName();
        }

        public void SetDemoTimeNameColors(bool doColor)
        {
            demoTimeNameColors = doColor;
            updateName();
        }
        public void SetClientNumNameAttach(bool doAttach)
        {
            attachClientNumToName = doAttach;
            updateName();
        }*/


        private void updateName()
        {
            if (client != null)
            {
                string nameToUse = _connectionOptions.userInfoName != null ? _connectionOptions.userInfoName : "Padawan";
                bool clientNumAlreadyAdded = false;
                if (_connectionOptions.demoTimeColorNames && client.Demorecording && !nameToUse.Contains("^"))
                {
                    DemoName_t demoName = client.getDemoName();
                    if(demoName != null) // Pointless I guess, hmm
                    {
                        DateTime demoStartTime = demoName.time;
                        string colorCodes = Convert.ToString(((DateTimeOffset)demoStartTime.ToUniversalTime()).ToUnixTimeSeconds(), 8);
                        while(colorCodes.Length < 12)
                        {
                            colorCodes = "0" + colorCodes;
                        }
                        if(colorCodes.Length > 12)
                        {
                            serverWindow.addToLog("Datetime Colorcode for name is more than 12 letters! Weird.", true);
                        }
                        else
                        {
                            if (jkaMode) // Lesss elegant but works I guess. JKA doesn't have background colors. TODO: Make this for 1.04 too.
                            {
                                string clientNumAddition = "";
                                if (_connectionOptions.attachClientNumToName) // For JKA we attach the clientnum here already so we can use it for the colors as well. Not elegant but better than filling with points more than necessary
                                {
                                    int clientNum = (client?.clientNum).GetValueOrDefault(-1);
                                    if (clientNum != -1)
                                    {
                                        clientNumAddition = $"({clientNum})";
                                        clientNumAlreadyAdded = true;
                                    }
                                }

                                int nameToUseLength = nameToUse.SpacelessStringLength();
                                while ((nameToUseLength + clientNumAddition.Length) < 12)
                                {
                                    nameToUse += ".";
                                    nameToUseLength++;
                                }
                                nameToUse += clientNumAddition;

                                StringBuilder tmpName = new StringBuilder();

                                int indexExtra = 0;
                                int i = 0;
                                for (i = 0; i < 12; i++)
                                {
                                    if (nameToUse[i + indexExtra].IsSpaceChar()) // Skip space characters
                                    {
                                        tmpName.Append(nameToUse[i + indexExtra]);
                                        indexExtra++;
                                        i--;
                                    } else
                                    {
                                        tmpName.Append("^");
                                        tmpName.Append(colorCodes[i]);
                                        tmpName.Append(nameToUse[i + indexExtra]);
                                    }
                                }
                                while (nameToUse.Length > (i+indexExtra))
                                {
                                    tmpName.Append(nameToUse[i + indexExtra]);
                                    i++;
                                }
                                //if (nameToUse.Length > 12)
                                //{
                                 //   tmpName.Append(nameToUse.Substring(12));
                                //}
                                nameToUse = tmpName.ToString();
                            } else
                            {

                                StringBuilder tmpName = new StringBuilder();

                                int nameToUseLength = nameToUse.SpacelessStringLength();
                                while (nameToUseLength < 6)
                                {
                                    nameToUse += ".";
                                    nameToUseLength++;
                                }
                                int indexExtra = 0;
                                int i = 0;
                                for (i = 0; i < 6; i++)
                                {
                                    if (nameToUse[i + indexExtra].IsSpaceChar()) // Skip space characters
                                    {
                                        tmpName.Append(nameToUse[i + indexExtra]);
                                        indexExtra++;
                                        i--;
                                    }
                                    else
                                    {
                                        tmpName.Append("^");
                                        tmpName.Append(colorCodes[i * 2]);
                                        tmpName.Append("^");
                                        tmpName.Append(colorCodes[i * 2 + 1]);
                                        tmpName.Append("^");
                                        tmpName.Append(colorCodes[i * 2]);
                                        tmpName.Append(nameToUse[i + indexExtra]);
                                    }
                                }
                                while (nameToUse.Length > (i + indexExtra))
                                {
                                    tmpName.Append(nameToUse[i + indexExtra]);
                                    i++;
                                }
                                //if (nameToUse.Length > 6)
                                //{
                                //    tmpName.Append(nameToUse.Substring(6));
                                //}
                                nameToUse = tmpName.ToString();
                            }
                        }
                    }
                }
                if (_connectionOptions.attachClientNumToName && !clientNumAlreadyAdded)
                {
                    int clientNum = (client?.clientNum).GetValueOrDefault(-1);
                    if(clientNum != -1)
                    {
                        nameToUse += $" ^7(^2{clientNum}^7)";
                    }
                }
                client.Name = nameToUse;
            }
        }
        
        private void updateSkin()
        {
            if (client != null)
            {
                string skinToUse = _connectionOptions.skin != null ? _connectionOptions.userInfoName : "kyle/default";
                
                client.Skin = skinToUse;
            }
        }

        private void createPeriodicReconnecter()
        {
            var tokenSource = new CancellationTokenSource();
            var ct = tokenSource.Token;
            Task.Factory.StartNew(() => { periodicReconnecter(ct); }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default).ContinueWith((t) => {
               serverWindow.addToLog(t.Exception.ToString(), true);
            }, TaskContinuationOptions.OnlyOnFaulted);
            backgroundTasks.Add(tokenSource);
        }

        private void periodicReconnecter(CancellationToken ct)
        {
            while (true)
            {
                System.Threading.Thread.Sleep(5*60*1000); // 5 minutes
                //ct.ThrowIfCancellationRequested();
                if (ct.IsCancellationRequested) return;

                if (client.Status != ConnectionStatus.Active && !trulyDisconnected)
                {
                    Reconnect();
                }
                if (client.Status == ConnectionStatus.Active && shouldBeRecordingADemo && !isRecordingADemo)
                {
                    serverWindow.addToLog("periodicReconnecter: Attempting to start/resume demo recording. (shouldBeRecordingADemo = true)");
                    startDemoRecord();
                }
            }
        }

        // !!!!TODO: If we are currently in an intermission, only allow whitelisted commands. Some servers will
        // turn any non whitelisted commands into a "say", particularly base, basejk and basejka servers.
        // Implement a way to delay commands so we can go on with the next one instead of retrying the same over and over.
        // Once intermission ends we can do others.
        // Also, when in an intermission, send an occasional click or 2 to end the intermission? Not that important tho i guess
        string[] intermissionCommandWhitelistJKA = new string[] { 
            
            // Server
            "userinfo","disconnect","cp","vdr","download","nextdl","stopdl","donedl",

            // Game always
            "say","say_team","tell","voice_cmd","score",

            // Bot commands
            // There's a bunch of bot related commands that seem to be ok too but have other restrictions. Whatever. Let's not allow them I guess

            // Game Intermission
            // Technically the below won't turn into a say but will give an error which isnt nice either.
            "give", "giveother", "god", "notarget", "noclip", "kill", "teamtask", "levelshot", "follow", "follownext", "followprev", "team", "duelteam", "siegeclass", "forcechanged", "where", "callvote", "vote", "callteamvote", "teamvote", "gc", "setviewpos", "stats" 
        };
        string[] intermissionCommandWhitelistJK2 = new string[] { 
            
            // Server
            "userinfo","disconnect","cp","vdr","download","nextdl","stopdl","donedl",

            // Game always
            "say","say_team","tell","vsay","vsay_team","vtell","vosay","vosay_team","votell","vtaunt","score",

            // Bot commands
            // There's a bunch of bot related commands that seem to be ok too but have other restrictions. Whatever. Let's not allow them I guess

            // Game Intermission
            // Technically the below won't turn into a say but will give an error which isnt nice either.
            "give", "god" ,"notarget" ,"noclip" ,"kill" ,"teamtask" ,"levelshot" ,"follow", "follownext", "followprev", "team", "forcechanged", "where", "callvote", "vote", "callteamvote", "teamvote", "gc", "setviewpos", "stats"
        };
        private void LeakyBucketRequester_CommandExecuting(object sender, LeakyBucketRequester<string, RequestCategory>.CommandExecutingEventArgs e)
        {
            // Check if the command is supported by server (it's just a crude array that gets elements added if server responds that a command is unsupported. Don't waste time, burst allowance, bandwidth and demo size sending useless commands).
            bool firstNonSpaceFound = false;
            int firstTrueSpace = -1;
            for(int i = 0; i < e.Command.Length; i++)
            {
                if(e.Command[i] == ' ')
                {
                    if (firstNonSpaceFound)
                    {
                        firstTrueSpace = i;
                        break;
                    }
                }
                else
                {
                    firstNonSpaceFound = true;
                }
            }
            string commandForValidityCheck = (firstTrueSpace != -1 ? e.Command.Substring(0, firstTrueSpace) : e.Command).Trim().ToLower();
            if (infoPool.unsupportedCommands.Contains(commandForValidityCheck))
            {
                e.Discard = true;
                return;
            }
            if (infoPool.isIntermission)
            {
                if (jkaMode)
                {
                    if (!intermissionCommandWhitelistJKA.Contains(commandForValidityCheck))
                    {
                        e.Delay = true;
                        e.NextTryAllowedIfDelayed = 500;
                        return;
                    }
                }
                else
                {
                    if (!intermissionCommandWhitelistJK2.Contains(commandForValidityCheck))
                    {
                        e.Delay = true;
                        e.NextTryAllowedIfDelayed = 500;
                        return;
                    }
                }
            }

            // Ok command is valid, let's see...
            if (client.Status == ConnectionStatus.Active) // safety check
            {
                int unacked = client.GetUnacknowledgedReliableCommandCount();
                if(unacked < 5)
                {

                    client.ExecuteCommand(e.Command);
                }
                else
                {
                    // If there is more than 5 unacked commands, let's just chill.
                    // This may happen due to bad connections.
                    // Benefit is, this way we may send less overall if the connection is bad, because
                    // Leakybucketrequester overwrites former commands with later ones of the same type.
                    // It should also allow a lower delay when sending commands because otherwise the queue
                    // may fill up to a crazy degree.
                    e.Cancel = true;
                }
            } else
            {
                e.Cancel = true;
            }
        }

        ~Connection()
        {
            CloseDown();
        }

        bool closedDown = false;
        Mutex closeDownMutex = new Mutex();

        public void CloseDown()
        {
            lock (closeDownMutex)
            {
                if (closedDown) return;
                closedDown = true;
                foreach (CancellationTokenSource backgroundTask in backgroundTasks)
                {
                    backgroundTask.Cancel();
                }
                _connectionOptions.PropertyChanged -= _connectionOptions_PropertyChanged;
                disconnect();
            }
        }

        private string ip;
        private ProtocolVersion protocol;

        private void afterConnect()
        {
            Status = client.Status;
            infoPool.MapName = client.ServerInfo.MapName;
            infoPool.teamInfo[(int)Team.Red].teamScore = client.GetMappedConfigstring(ClientGame.Configstring.Scores1).Atoi();
            infoPool.ScoreRed = client.GetMappedConfigstring(ClientGame.Configstring.Scores1).Atoi();
            infoPool.teamInfo[(int)Team.Blue].teamScore = client.GetMappedConfigstring(ClientGame.Configstring.Scores2).Atoi();
            infoPool.ScoreBlue = client.GetMappedConfigstring(ClientGame.Configstring.Scores2).Atoi();
            EvaluateFlagStatus(client.GetMappedConfigstring(ClientGame.Configstring.FlagStatus));
        }

        private async Task<bool> createConnection( string ipA, ProtocolVersion protocolA,int timeOut = 30000)
        {
            if (closedDown) return false;

            trulyDisconnected = false;

            ip = ipA;
            protocol = protocolA;

            IClientHandler handler = null;
            if(protocol == ProtocolVersion.Protocol15)
            {
                handler = new JOClientHandler(ProtocolVersion.Protocol15, ClientVersion.JO_v1_02);
            } else if(protocol == ProtocolVersion.Protocol16)
            {
                handler = new JOClientHandler(ProtocolVersion.Protocol16, ClientVersion.JO_v1_04);
            } else if(protocol == ProtocolVersion.Protocol26)
            {
                handler = new JAClientHandler(ProtocolVersion.Protocol26, ClientVersion.JA_v1_01);
            } else
            {
                serverWindow.addToLog($"ERROR: Tried to create connection using protocol {protocol}. Not supported.",true);
                return false;
            }
            client = new Client(handler) { GhostPeer = this.GhostPeer }; // Todo make more flexible
            //client.Name = "Padawan";
            client.Name = _connectionOptions.userInfoName == null ? "Padawan" : _connectionOptions.userInfoName;
            if (jkaMode) // TODO Detect mods and proceed accordingly
            {
                CheckSumFile[] checkSumFiles = new CheckSumFile[]{
                    new CheckSumFile(){ headerLongData = Helpers.GetResourceData("hl/basejka/assets0.hl")},
                    new CheckSumFile(){ headerLongData = Helpers.GetResourceData("hl/basejka/assets1.hl")},
                    new CheckSumFile(){ headerLongData = Helpers.GetResourceData("hl/basejka/assets2.hl")},
                    new CheckSumFile(){ headerLongData = Helpers.GetResourceData("hl/basejka/assets3.hl"),hasCgame=true,hasUI=true},
                }; 
                client.SetAssetChecksumFiles(checkSumFiles);
            }
            else
            {
                CheckSumFile[] checkSumFiles = null;
                // TODO Fix this if we ever allow connecting to 1.03/1.04
                if (protocol == ProtocolVersion.Protocol15) // JK2 1.02 
                {
                    checkSumFiles = new CheckSumFile[]{
                        new CheckSumFile(){ headerLongData = Helpers.GetResourceData("hl/basejk2/assets0.hl")},
                        new CheckSumFile(){ headerLongData = Helpers.GetResourceData("hl/basejk2/assets1.hl"),hasCgame=true,hasUI=true},
                    };
                }
                else if ( protocol == ProtocolVersion.Protocol16) // JK2 1.04
                {
                    checkSumFiles = new CheckSumFile[]{
                        new CheckSumFile(){ headerLongData = Helpers.GetResourceData("hl/basejk2/assets0.hl")},
                        new CheckSumFile(){ headerLongData = Helpers.GetResourceData("hl/basejk2/assets1.hl")},
                        new CheckSumFile(){ headerLongData = Helpers.GetResourceData("hl/basejk2/assets2.hl")},
                        new CheckSumFile(){ headerLongData = Helpers.GetResourceData("hl/basejk2/assets5.hl"),hasCgame=true,hasUI=true},
                    };
                } else // JK2 1.03 // TODO Detect this properly here.
                {
                    checkSumFiles = new CheckSumFile[]{
                        new CheckSumFile(){ headerLongData = Helpers.GetResourceData("hl/basejk2/assets0.hl")},
                        new CheckSumFile(){ headerLongData = Helpers.GetResourceData("hl/basejk2/assets1.hl")},
                        new CheckSumFile(){ headerLongData = Helpers.GetResourceData("hl/basejk2/assets2.hl"),hasCgame=true,hasUI=true},
                    };
                } 
                client.SetAssetChecksumFiles(checkSumFiles);
            }

            if(password != null)
            {
                client.Password = password;
            }

            client.ServerCommandExecuted += ServerCommandExecuted;
            client.ServerInfoChanged += Connection_ServerInfoChanged;
            client.SnapshotParsed += Client_SnapshotParsed;
            client.EntityEvent += Client_EntityEvent;
            client.Disconnected += Client_Disconnected;
            client.UserCommandGenerated += Client_UserCommandGenerated;
            client.DebugEventHappened += Client_DebugEventHappened;
            clientStatistics = client.Stats;
            Status = client.Status;
            
            client.Start(ExceptionCallback);
            Status = client.Status;

            try
            {

                //Task connectTask = client.Connect(ip, protocol);
                Task connectTask = client.Connect(ip);
                bool didConnect = false;
                await Task.Run(()=> {
                    try
                    {

                        didConnect = connectTask.Wait(timeOut);
                    } catch(TaskCanceledException e)
                    {
                        // Who cares.
                        didConnect = false;
                    }
                });
                if (!didConnect)
                {
                    Status = client.Status;
                    serverWindow.addToLog($"Failed to create connection. Timeout after {timeOut} milliseconds. May still connect who knows.", true);
                    connectTask.ContinueWith((a)=> {
                        Status = client.Status;
                        if (shouldBeRecordingADemo)
                        {
                            serverWindow.addToLog("createConnection: Attempting to start/resume demo recording after delayed connect. (shouldBeRecordingADemo = true)");
                            startDemoRecord();
                        }
                        afterConnect();
                    },TaskContinuationOptions.NotOnCanceled);
                    return false;
                } 

            } catch(Exception e)
            {
                Status = client.Status;
                serverWindow.addToLog("Failed to create connection: "+e.ToString(),true);
                return false;
            }
            Status = client.Status;
            if (shouldBeRecordingADemo)
            {
                serverWindow.addToLog("createConnection: Attempting to start/resume demo recording. (shouldBeRecordingADemo = true)");
                startDemoRecord();
            }
            afterConnect();

            serverWindow.addToLog("New connection created.");
            return true;
        }

        private void Client_DebugEventHappened(object sender, object e)
        {
            if(e is ConfigStringMismatch)
            {
                ConfigStringMismatch info = (ConfigStringMismatch)e;
                serverWindow.addToLog($"DEBUG: Config string mismatch: \"{info.intendedString}\" became \"{info.actualString}\"",true);
                Task.Run(()=> {
                    MemoryStream ms = new MemoryStream();
                    ms.Write(Encoding.UTF8.GetBytes($"{info.intendedString}\n{info.actualString}\n"));
                    if(info.oldGsStringData != null)
                    {
                        ms.Write(info.oldGsStringData);
                        ms.Write(Encoding.UTF8.GetBytes($"\n"));
                    }
                    if(info.newGsStringData != null)
                    {
                        ms.Write(info.newGsStringData);
                        ms.Write(Encoding.UTF8.GetBytes($"\n"));
                    }
                    Helpers.logToSpecificDebugFile(ms.ToArray(),"configStringMismatch.data");
                });
            } else if(e is NetDebug)
            {
                NetDebug nb = (NetDebug)e;
                Helpers.logToSpecificDebugFile(new string[] {nb.debugString },"netDebug.log",true);
            }
        }

        DateTime lastForcedActivity = DateTime.Now;

        int doClicks = 0;
        bool lastWasClick = false;

        // We relay this so any potential watchers can latch on to this and do their own modifications if they want to.
        // It also means we don't have to have watchers subscribe directly to the client because then that would break
        // when we get disconnected/reconnected etc.
        private void Client_UserCommandGenerated(object sender, ref UserCommand modifiableCommand, in UserCommand previousCommand)
        {
            if (amNotInSpec)
            {
                DoSillyThings(ref modifiableCommand, in previousCommand);
            } else
            {
                if ((DateTime.Now - lastForcedActivity).TotalMilliseconds > 60000) // Avoid getting inactivity dropped, so just send a single forward move once a minute.
                {
                    modifiableCommand.ForwardMove = 127;
                    lastForcedActivity = DateTime.Now;
                }
            }
            if(this.CameraOperator == null)
            {
                if(doClicks > 0 && lastWasClick == false)
                {
                    doClicks--;
                    lastWasClick = true;
                    modifiableCommand.Buttons |= (int)UserCommand.Button.Attack;
                    modifiableCommand.Buttons |= (int)UserCommand.Button.AnyJK2; // AnyJK2 simply means Any, but its the JK2 specific constant
                }
                else
                {
                    lastWasClick = false;
                }
            }
            OnClientUserCommandGenerated(ref modifiableCommand, in previousCommand);
        }

        int reconnectTriesCount = 0;
        const int reconnectMaxTries = 10;

        int DisconnectCallbackRecursion = 0;
        const int DisconnectCallbackRecursionLimit = 10;

        public async Task<bool> hardReconnect()
        {
            Status = client.Status;
            bool success = false;
            while (success == false)
            {
                int delay = 1000 + (int)(1000 * Math.Pow(2, reconnectTriesCount));
                System.Threading.Thread.Sleep(delay); // The more retries fail, the larger the delay between tries grows.
                serverWindow.addToLog($"Reconnect try {reconnectTriesCount+1}. Delay {delay} ms.");
                if (reconnectTriesCount >= reconnectMaxTries)
                {
                    serverWindow.addToLog($"Giving up on reconnect after {reconnectTriesCount} tries.", true);
                    break;
                }
                if (client.Status == ConnectionStatus.Active) // Don't try to reconnect if we somehow managed to already reconnect in some other way.
                {
                    break;
                }
                Status = client.Status;
                success = await Reconnect();
                Status = client.Status;
                if (!success)
                {
                    reconnectTriesCount++;
                } else
                {
                    reconnectTriesCount = 0;
                }
            }
            reconnectTriesCount = 0;
            Status = client.Status;
            return success;
        }

        public async Task<bool> Reconnect()
        {
            disconnect();
            return await createConnection(ip, protocol);
        }

        private async void Client_Disconnected(object sender, EventArgs e)
        {
            if (DisconnectCallbackRecursion++ > DisconnectCallbackRecursionLimit)
            {
                serverWindow.addToLog("[Client_Disconnected] Hit Disconnect recursion limit trying to restart the connection. Giving up.", true);
                return;
            }

            serverWindow.addToLog("Involuntary disconnect for some reason.", true);
            Status = client.Status;

            if (isRecordingADemo)
            {
                wasRecordingADemo = true;
            }
            if (wasRecordingADemo)
            {
                serverWindow.addToLog("Was recording a demo. Stopping recording if not already stopped.");
                stopDemoRecord(true);
            }

            // Reconnect
            System.Threading.Thread.Sleep(1000);
            serverWindow.addToLog("Attempting to reconnect.");

            //client.Start(ExceptionCallback); // I think that's only necessary once?
            //Status = client.Status;

            /*await client.Connect(ip, protocol); // TODO This can get cancelled. In that case,  handle it somehow.
             */
            // Be safe and just reset everything
            if (await hardReconnect())
            {
                serverWindow.addToLog("Reconnected.");

                if (wasRecordingADemo || shouldBeRecordingADemo)
                {
                    serverWindow.addToLog("Attempting to resume demo recording.");
                    startDemoRecord();
                }
            }
            DisconnectCallbackRecursion--;

        }

        bool wasRecordingADemo = false;
        bool shouldBeRecordingADemo = false;

        // Client crashed for some reason
        private async Task ExceptionCallback(JKClientException exception)
        {
            if (DisconnectCallbackRecursion++ > DisconnectCallbackRecursionLimit)
            {
                serverWindow.addToLog("[ExceptionCallback] Hit Disconnect recursion limit trying to restart the connection. Giving up.", true);
                return;
            }
            serverWindow.addToLog("JKClient crashed: " + exception.ToString(),true);
            Debug.WriteLine(exception);

            if (isRecordingADemo)
            {
                wasRecordingADemo = true;
            }
            if (wasRecordingADemo)
            {
                serverWindow.addToLog("Was recording a demo. Stopping recording if not already stopped.");
                stopDemoRecord(true);
            }

            if (closedDown) return;

            // Reconnect
            System.Threading.Thread.Sleep(1000);
            /*serverWindow.addToLog("Attempting to restart.");

            client.Start(ExceptionCallback); // I think that's only necessary once?
            Status = client.Status;

            serverWindow.addToLog("Attempting to reconnect.");
            await client.Connect(ip, protocol);
            Status = client.Status;*/

            serverWindow.addToLog("Attempting to reconnect.");

            // Be safe and just reset everything
            if (await hardReconnect())
            {
                serverWindow.addToLog("Reconnected.");

                if (wasRecordingADemo || shouldBeRecordingADemo)
                {
                    serverWindow.addToLog("Attempting to resume demo recording.");
                    startDemoRecord();
                }
            }
            DisconnectCallbackRecursion--;
        }

        TextInfo textInfo = CultureInfo.InvariantCulture.TextInfo;

        int lastEventSnapshotNumber = 0;
        Dictionary<int, Vector3> thisSnapshotObituaryVictims = new Dictionary<int, Vector3>();
        Dictionary<int, Vector3> thisSnapshotObituaryAttackers = new Dictionary<int, Vector3>();
        private unsafe void Client_EntityEvent(object sender, EntityEventArgs e)
        {
            int snapshotNumber, serverTime;
            ((IJKClientImport)client).GetCurrentSnapshotNumber(out snapshotNumber, out serverTime);

            if(snapshotNumber != lastEventSnapshotNumber)
            {
                thisSnapshotObituaryVictims.Clear();
                thisSnapshotObituaryAttackers.Clear();
            }

            if (e.EventType == ClientGame.EntityEvent.Obituary) // TODO Fix it up for JKA
            {
                // TODO Important. See if we can correlate death events to ctf frag events. That way we could know where
                //  the flag carrier was killed and thus where the flag is
                // We know the death event comes first. If we just pass the snapshotnumber, we can correlate them.
                // Todo do more elaborate logging. Death method etc. Detect multikills maybe
                int target = e.Entity.CurrentState.OtherEntityNum;
                int attacker = e.Entity.CurrentState.OtherEntityNum2;

                if(attacker >= 0 && attacker < client.ClientHandler.MaxClients && attacker != target && this.IsMainChatConnection) // Kill tracking, only do on one connection to keep things consistent.
                {
                    lock (infoPool.killTrackers) { // Just in case unlucky timing and mainchatconnection changes :) 
                        infoPool.playerInfo[attacker].chatCommandTrackingStuff.totalKills++;
                        infoPool.playerInfo[target].chatCommandTrackingStuff.totalDeaths++;
                        infoPool.killTrackers[attacker, target].kills++;
                        infoPool.killTrackers[attacker, target].lastKillTime = DateTime.Now;
                        bool killTrackersSynced = infoPool.killTrackers[attacker, target].trackingMatch && infoPool.killTrackers[target, attacker].trackingMatch && infoPool.killTrackers[attacker, target].trackedMatchKills == infoPool.killTrackers[target, attacker].trackedMatchDeaths && infoPool.killTrackers[attacker, target].trackedMatchDeaths == infoPool.killTrackers[target, attacker].trackedMatchKills;
                        if (infoPool.killTrackers[attacker, target].trackingMatch)
                        {
                            infoPool.killTrackers[attacker, target].trackedMatchKills++;
                            if(!killTrackersSynced && !_connectionOptions.silentMode) { 
                                leakyBucketRequester.requestExecution($"tell {attacker} ^7^7^7Match against {infoPool.playerInfo[target].name}^7^7^7: {infoPool.killTrackers[attacker, target].trackedMatchKills}-{infoPool.killTrackers[attacker, target].trackedMatchDeaths}",RequestCategory.KILLTRACKER,0, ChatMemeCommandsDelay, LeakyBucketRequester<string, RequestCategory>.RequestBehavior.ENQUEUE,null,null);
                            }
                        }
                        if (infoPool.killTrackers[target, attacker].trackingMatch)
                        {
                            infoPool.killTrackers[target, attacker].trackedMatchDeaths++;
                            if (!killTrackersSynced && !_connectionOptions.silentMode)
                            {
                                leakyBucketRequester.requestExecution($"tell {attacker} ^7^7^7Match against {infoPool.playerInfo[target].name}^7^7^7: {infoPool.killTrackers[attacker, target].trackedMatchKills}-{infoPool.killTrackers[attacker, target].trackedMatchDeaths}", RequestCategory.KILLTRACKER, 0, ChatMemeCommandsDelay, LeakyBucketRequester<string, RequestCategory>.RequestBehavior.ENQUEUE, null, null);
                            }
                        }
                        if(killTrackersSynced && !_connectionOptions.silentMode)
                        {
                            int smallerClientNum = Math.Min(attacker, target); // Keep the public kill tracker always in same order.
                            int biggerClientNum = Math.Max(attacker, target);
                            leakyBucketRequester.requestExecution($"say ^7^7^7Match {infoPool.playerInfo[smallerClientNum].name} ^7^7^7vs. {infoPool.playerInfo[biggerClientNum].name}^7^7^7: {infoPool.killTrackers[smallerClientNum, biggerClientNum].trackedMatchKills}-{infoPool.killTrackers[smallerClientNum, biggerClientNum].trackedMatchDeaths}", RequestCategory.KILLTRACKER, 0, ChatMemeCommandsDelay, LeakyBucketRequester<string, RequestCategory>.RequestBehavior.ENQUEUE, null, null);
                        }
                    }
                }

                ClientEntity copyOfEntity = e.Entity; // This is necessary in order to read the fixed float arrays. Don't ask why, idk.
                Vector3 locationOfDeath;
                locationOfDeath.X = copyOfEntity.CurrentState.Position.Base[0];
                locationOfDeath.Y = copyOfEntity.CurrentState.Position.Base[1];
                locationOfDeath.Z = copyOfEntity.CurrentState.Position.Base[2];
                MeansOfDeath mod = (MeansOfDeath)e.Entity.CurrentState.EventParm;
                if (target < 0 || target >= client.ClientHandler.MaxClients)
                {
                    serverWindow.addToLog("EntityEvent Obituary: value "+target+" is out of bounds.");
                    return;
                }

                // Was it the flag carrier?
                foreach (int teamToCheck in Enum.GetValues(typeof(Team)))
                {
                    if (infoPool.teamInfo[teamToCheck].lastFlagCarrierUpdate != null && infoPool.teamInfo[teamToCheck].lastFlagCarrier == target && infoPool.teamInfo[teamToCheck].lastFlagCarrierValid)
                    {
                        infoPool.teamInfo[teamToCheck].flagDroppedPosition = locationOfDeath;
                        infoPool.teamInfo[teamToCheck].lastFlagDroppedPositionUpdate = DateTime.Now;
                        // Remmeber flag carrier deaths so we can keep the camera nearby for a bit longer if it was a relevant manner of death (everything that's not suicide)
                        if(attacker != target) // If it was suicide we don't care
                        {
                            if (attacker < 0 || attacker >= client.ClientHandler.MaxClients)
                            {
                                infoPool.teamInfo[teamToCheck].lastFlagCarrierWorldDeath = DateTime.Now; // This could be unintentional so still funny and interesting
                            }
                            else  // was a normal frag
                            {
                                infoPool.teamInfo[teamToCheck].lastFlagCarrierFragged = DateTime.Now;
                            }
                        }
                        
                            
                    }
                }

                thisSnapshotObituaryVictims.Add(target, locationOfDeath);

                infoPool.playerInfo[target].IsAlive = false;
                infoPool.playerInfo[target].lastDeathPosition = locationOfDeath;
                infoPool.playerInfo[target].lastDeath = DateTime.Now;
                infoPool.playerInfo[target].position = locationOfDeath;
                infoPool.playerInfo[target].lastPositionUpdate = DateTime.Now;
                string targetName = infoPool.playerInfo[target].name;


                if (this.IsMainChatConnection && MeansOfDeath.MOD_FALLING == mod && attacker!=target && attacker>=0 && attacker< client.ClientHandler.MaxClients)
                {
                    infoPool.playerInfo[attacker].chatCommandTrackingStuff.doomkills++;
                }

                string killString = null;
                bool generic = false;
                switch (mod)
                {
                    case MeansOfDeath.MOD_STUN_BATON:
                        killString = "stunned";
                        break;
                    case MeansOfDeath.MOD_MELEE:
                        killString = "beat down";
                        break;
                    case MeansOfDeath.MOD_SABER:
                        killString = "sabered";
                        break;
                    case MeansOfDeath.MOD_BRYAR_PISTOL:
                    case MeansOfDeath.MOD_BRYAR_PISTOL_ALT:
                    case MeansOfDeath.MOD_BLASTER:
                    case MeansOfDeath.MOD_BOWCASTER:
                    case MeansOfDeath.MOD_REPEATER:
                    case MeansOfDeath.MOD_REPEATER_ALT:
                    case MeansOfDeath.MOD_REPEATER_ALT_SPLASH:
                    case MeansOfDeath.MOD_DEMP2:
                    case MeansOfDeath.MOD_DEMP2_ALT:
                    case MeansOfDeath.MOD_FLECHETTE:
                    case MeansOfDeath.MOD_FLECHETTE_ALT_SPLASH:
                        killString = "shot";
                        generic = true;
                        break;
                    case MeansOfDeath.MOD_DISRUPTOR:
                    case MeansOfDeath.MOD_DISRUPTOR_SPLASH:
                    case MeansOfDeath.MOD_DISRUPTOR_SNIPER:
                        generic = true;
                        killString = "sniped";
                        break;
                    case MeansOfDeath.MOD_ROCKET:
                    case MeansOfDeath.MOD_ROCKET_SPLASH:
                    case MeansOfDeath.MOD_ROCKET_HOMING:
                    case MeansOfDeath.MOD_ROCKET_HOMING_SPLASH:
                        generic = true;
                        killString = "rocketed";
                        break;
                    case MeansOfDeath.MOD_THERMAL:
                    case MeansOfDeath.MOD_THERMAL_SPLASH:
                    case MeansOfDeath.MOD_DET_PACK_SPLASH:
                        generic = true;
                        killString = "detonated";
                        break;
                    case MeansOfDeath.MOD_TRIP_MINE_SPLASH:
                    case MeansOfDeath.MOD_TIMED_MINE_SPLASH:
                        generic = true;
                        killString = "tripped";
                        break;
                    case MeansOfDeath.MOD_FORCE_DARK:
                        killString = "annihilated";
                        break;
                    case MeansOfDeath.MOD_SENTRY:
                        killString = "sentry-killed";
                        break;
                    case MeansOfDeath.MOD_WATER:
                        killString = "drowned";
                        break;
                    case MeansOfDeath.MOD_SLIME:
                        killString = "slimed";
                        break;
                    case MeansOfDeath.MOD_LAVA:
                        killString = "lava-burned";
                        break;
                    case MeansOfDeath.MOD_CRUSH:
                        killString = "crushed";
                        break;
                    case MeansOfDeath.MOD_TELEFRAG:
                        killString = "admin-killed";
                        break;
                    case MeansOfDeath.MOD_FALLING:
                        killString = "doomed";
                        break;
                    case MeansOfDeath.MOD_SUICIDE:
                        killString = "anheroed";
                        break;
                    case MeansOfDeath.MOD_TARGET_LASER:
                        killString = "lasered";
                        break;
                    case MeansOfDeath.MOD_TRIGGER_HURT:
                        killString = "triggered";
                        break;
                    case MeansOfDeath.MOD_MAX:
                        break;
                    case MeansOfDeath.MOD_UNKNOWN:
                    default:
                        break;
                }

                if (attacker < 0 || attacker >= client.ClientHandler.MaxClients)
                {
                    serverWindow.addToLog(targetName + " was "+ (killString == null ? "killed" : killString) + (killString == null || generic ? " [" + mod.ToString() + "]" : ""));
                } else
                {
                    thisSnapshotObituaryAttackers.Add(attacker, locationOfDeath);
                    infoPool.playerInfo[attacker].position = locationOfDeath;
                    infoPool.playerInfo[attacker].lastPositionUpdate = DateTime.Now;
                    // Can we also set the setalive of the attacker here? he might have blown himself up too.
                    // Would his self blowup message come before or after this?
                    string attackerName = infoPool.playerInfo[attacker].name;
                    serverWindow.addToLog(attackerName + " "+(killString == null ? "killed" : killString)+" " +( (target==attacker)? "himself": targetName) + (killString == null || generic? " [" + mod.ToString() + "]" : ""));
                }
            } else if(e.EventType == ClientGame.EntityEvent.CtfMessage)
            {
                CtfMessageType messageType = (CtfMessageType)e.Entity.CurrentState.EventParm;
                int playerNum = e.Entity.CurrentState.TrickedEntityIndex;
                Team team = (Team)e.Entity.CurrentState.TrickedEntityIndex2;

                if (team != Team.Red && team != Team.Blue)
                {
                    // Some other team, weird.
                    return;
                }

                Team otherTeam = team == Team.Red ? Team.Blue : Team.Red;

                string teamAsString = "";
                string otherTeamAsString = "";
                switch (team)
                {
                    case Team.Blue:
                        teamAsString = "blue";
                        otherTeamAsString = "red";
                        break;
                    case Team.Red:
                        teamAsString = "red";
                        otherTeamAsString = "blue";
                        break;
                    default:break;
                }

                PlayerInfo pi = null;

                if(playerNum >= 0 && playerNum <= client.ClientHandler.MaxClients)
                {
                    pi = infoPool.playerInfo[playerNum];
                }

                // If it was picked up or generally status changed, and it was at base before, remember this as the last time it was at base.
                if (infoPool.teamInfo[(int)team].flag == FlagStatus.FLAG_ATBASE)
                {
                    infoPool.teamInfo[(int)team].lastTimeFlagWasSeenAtBase = DateTime.Now;
                }

                if (messageType == CtfMessageType.PlayerGotFlag && pi != null)
                {
                    infoPool.teamInfo[(int)team].lastFlagCarrier = playerNum;
                    infoPool.teamInfo[(int)team].lastFlagCarrierUpdate = DateTime.Now;
                    infoPool.teamInfo[(int)team].lastFlagCarrierValid = true;
                    infoPool.teamInfo[(int)team].lastFlagCarrierValidUpdate = DateTime.Now;
                    infoPool.teamInfo[(int)team].flag = FlagStatus.FLAG_TAKEN;
                    infoPool.teamInfo[(int)team].lastFlagUpdate = DateTime.Now;
                    serverWindow.addToLog(pi.name + " got the " + teamAsString + " flag.");

                } else if (messageType == CtfMessageType.FraggedFlagCarrier && pi != null)
                {
                    // Teams are inverted here because team is the team of the person who got killed
                    infoPool.teamInfo[(int)otherTeam].flag = FlagStatus.FLAG_DROPPED;
                    infoPool.teamInfo[(int)otherTeam].lastFlagCarrierValid = false;
                    infoPool.teamInfo[(int)otherTeam].lastFlagCarrierValidUpdate = DateTime.Now;
                    infoPool.teamInfo[(int)otherTeam].lastFlagUpdate = DateTime.Now;
                    infoPool.teamInfo[(int)otherTeam].lastFlagCarrierFragged = DateTime.Now;
                    if ((client.Entities?[playerNum].CurrentValid).GetValueOrDefault(false)) // Player who did kill is currently visible!
                    {
                        // We know where the flag is!
                        Vector3 locationOfFrag;
                        locationOfFrag.X = (client.Entities?[playerNum].CurrentState.Position.Base[0]).GetValueOrDefault(0.0f);
                        locationOfFrag.Y = (client.Entities?[playerNum].CurrentState.Position.Base[1]).GetValueOrDefault(0.0f);
                        locationOfFrag.Z = (client.Entities?[playerNum].CurrentState.Position.Base[2]).GetValueOrDefault(0.0f);
                        infoPool.teamInfo[(int)otherTeam].flagDroppedPosition = locationOfFrag;
                        infoPool.teamInfo[(int)otherTeam].lastFlagDroppedPositionUpdate = DateTime.Now;
                    } else if (thisSnapshotObituaryAttackers.ContainsKey(playerNum))
                    {
                        // We remember the death message. It had a position. We can use that. :)
                        infoPool.teamInfo[(int)otherTeam].flagDroppedPosition = thisSnapshotObituaryAttackers[playerNum];
                        infoPool.teamInfo[(int)otherTeam].lastFlagDroppedPositionUpdate = DateTime.Now;
                    } 
                    serverWindow.addToLog(pi.name + " killed carrier of " + otherTeamAsString + " flag.");
                    
                } else if (messageType == CtfMessageType.FlagReturned)
                {
                    infoPool.teamInfo[(int)team].flag = FlagStatus.FLAG_ATBASE;
                    infoPool.teamInfo[(int)team].lastFlagCarrierValid = false;
                    infoPool.teamInfo[(int)team].lastFlagCarrierValidUpdate = DateTime.Now;
                    infoPool.teamInfo[(int)team].lastFlagUpdate = DateTime.Now;
                    infoPool.teamInfo[(int)team].lastTimeFlagWasSeenAtBase = DateTime.Now;
                    serverWindow.addToLog(textInfo.ToTitleCase(teamAsString) + " flag was returned.");
                }
                else if (messageType == CtfMessageType.PlayerCapturedFlag && pi != null)
                {
                    infoPool.teamInfo[(int)team].flag = FlagStatus.FLAG_ATBASE;
                    infoPool.teamInfo[(int)team].lastFlagCarrierValid = false;
                    infoPool.teamInfo[(int)team].lastFlagCarrierValidUpdate = DateTime.Now;
                    infoPool.teamInfo[(int)team].lastFlagUpdate = DateTime.Now;
                    infoPool.teamInfo[(int)team].lastTimeFlagWasSeenAtBase = DateTime.Now;
                    serverWindow.addToLog(pi.name + " captured the "+teamAsString+" flag.");
                }
                else if (messageType == CtfMessageType.PlayerReturnedFlag && pi != null)
                {
                    infoPool.teamInfo[(int)team].flag = FlagStatus.FLAG_ATBASE;
                    infoPool.teamInfo[(int)team].lastFlagCarrierValid = false;
                    infoPool.teamInfo[(int)team].lastFlagCarrierValidUpdate = DateTime.Now;
                    infoPool.teamInfo[(int)team].lastFlagUpdate = DateTime.Now;
                    infoPool.teamInfo[(int)team].lastTimeFlagWasSeenAtBase = DateTime.Now;
                    serverWindow.addToLog(pi.name + " returned the " + teamAsString + " flag.");
                }

            }
            else if (e.EventType == ClientGame.EntityEvent.ForceDrained)
            {
                int targetNum = e.Entity.CurrentState.Owner;
                if(targetNum >= 0 && targetNum < client.ClientHandler.MaxClients)
                {
                    infoPool.playerInfo[targetNum].lastDrainedEvent = DateTime.Now;
                }
            }
            else if (e.EventType == ClientGame.EntityEvent.Jump)
            {
                if(e.Entity.CurrentState.Number == ClientNum)
                {
                    //jumpReleasedThisJump = false;
                    countFramesJumpReleasedThisJump = 0;
                }
            }
            // Todo: look into various sound events that are broarcast to everyone, also global item pickup,
            // then we immediately know who's carrying the flag
        }

        private void snapsEnforcementUpdate()
        {

            if (snapsSettings != null)
            {
                client.AfkDropSnaps = snapsSettings.forceAFKSnapDrop;
                client.AfkDropSnapsMinFPS = snapsSettings.afkMaxSnaps;
                if (snapsSettings.forceEmptySnaps && infoPool.NoActivePlayers)
                {
                    client.ClientForceSnaps = true;
                    client.DesiredSnaps = snapsSettings.emptySnaps;
                } else if (snapsSettings.forceBotOnlySnaps && (infoPool.lastBotOnlyConfirmed != null && (DateTime.Now-infoPool.lastBotOnlyConfirmed.Value).TotalMilliseconds < 10000 || infoPool.botOnlyGuaranteed) )
                {
                    client.ClientForceSnaps = true;
                    client.DesiredSnaps = snapsSettings.botOnlySnaps;
                } else
                {
                    client.ClientForceSnaps = false;
                    client.DesiredSnaps = 1000;
                }
            }
            else
            {
                client.ClientForceSnaps = false;
                client.DesiredSnaps = 1000;
            }
        }

        protected void OnPropertyChanged(PropertyChangedEventArgs eventArgs)
        {
            PropertyChanged?.Invoke(this, eventArgs);
        }


        int lastRequestedAlwaysFollowSpecClientNum = -1;
        DateTime[] clientsWhoDontWantTOrCannotoBeSpectated = new DateTime[32] { // Looool this is cringe xd
            DateTime.Now.AddYears(-1), DateTime.Now.AddYears(-1), DateTime.Now.AddYears(-1), DateTime.Now.AddYears(-1), DateTime.Now.AddYears(-1), 
            DateTime.Now.AddYears(-1), DateTime.Now.AddYears(-1), DateTime.Now.AddYears(-1), DateTime.Now.AddYears(-1), DateTime.Now.AddYears(-1), 
            DateTime.Now.AddYears(-1), DateTime.Now.AddYears(-1), DateTime.Now.AddYears(-1), DateTime.Now.AddYears(-1), DateTime.Now.AddYears(-1), 
            DateTime.Now.AddYears(-1), DateTime.Now.AddYears(-1), DateTime.Now.AddYears(-1), DateTime.Now.AddYears(-1), DateTime.Now.AddYears(-1), 
            DateTime.Now.AddYears(-1), DateTime.Now.AddYears(-1), DateTime.Now.AddYears(-1), DateTime.Now.AddYears(-1), DateTime.Now.AddYears(-1), 
            DateTime.Now.AddYears(-1), DateTime.Now.AddYears(-1), DateTime.Now.AddYears(-1), DateTime.Now.AddYears(-1), DateTime.Now.AddYears(-1), 
            DateTime.Now.AddYears(-1), DateTime.Now.AddYears(-1), 
        };

        public bool[] entityOrPSVisible = new bool[Common.MaxGEntities];

        private Vector3 delta_angles;
        private float baseSpeed = 0;
        public float Speed { get; private set; } = 0;
        private int saberDrawAnimLevel = -1;

        private Snapshot lastSnapshot = new Snapshot();
        private PlayerState lastPlayerState = new PlayerState();
        private int lastSnapNum = -1;

        public DateTime? lastAnyPositionOrAngleChange; // Last time the player position or angle changed
        private unsafe void Client_SnapshotParsed(object sender, SnapshotParsedEventArgs e)
        {

            snapsEnforcementUpdate();

            SnapStatus.addDataPoint(client.SnapNum,client.ServerTime);
            OnPropertyChanged(new PropertyChangedEventArgs("SnapStatus"));

            infoPool.setGameTime(client.gameTime);
            //infoPool.isIntermission = client.IsInterMission;
            Snapshot snap = e.snap;
            lastSnapshot = snap;
            lastPlayerState = snap.PlayerState;
            lastSnapNum = e.snapNum;
            bool isIntermission = snap.PlayerState.PlayerMoveType == JKClient.PlayerMoveType.Intermission;
            infoPool.isIntermission = isIntermission;
            this.Speed = e.snap.PlayerState.Speed;
            PlayerMoveType = snap.PlayerState.PlayerMoveType;

            if (isDuelMode && isIntermission)
            {
                doClicks = Math.Min(3, doClicks + 1);
            }

            SpectatedPlayer = client.playerStateClientNum; // Might technically need a playerstate parsed event but ig this will do?

            int[] snapEntityMapping = new int[Common.MaxGEntities];
            for(int i = 0; i < Common.MaxGEntities; i++)
            {
                snapEntityMapping[i] = -1;
            }
            for(int i = 0; i < e.snap.NumEntities; i++)
            {
                snapEntityMapping[e.snap.Entities[i].Number] = i;
            }
            entityOrPSVisible[snap.PlayerState.ClientNum] = true;
            //ClientEntity[] entities = client.Entities;
            //if (entities == null)
            //{
            //    return;
            //}

            int EFDeadFlag = jkaMode ? (int)JKAStuff.EntityFlags.EF_DEAD : (int)JOStuff.EntityFlags.EF_DEAD;
            int PWRedFlag = jkaMode ? (int)JKAStuff.ItemList.powerup_t.PW_REDFLAG : (int)JOStuff.ItemList.powerup_t.PW_REDFLAG;
            int PWBlueFlag = jkaMode ? (int)JKAStuff.ItemList.powerup_t.PW_BLUEFLAG : (int)JOStuff.ItemList.powerup_t.PW_BLUEFLAG;
            int ETTeam = jkaMode ? (int)JKAStuff.entityType_t.ET_TEAM : (int)JOStuff.entityType_t.ET_TEAM;
            int ETItem = jkaMode ? (int)JKAStuff.entityType_t.ET_ITEM : (int)JOStuff.entityType_t.ET_ITEM;
            //int EFBounceHalf = jkaMode ? 0 : (int)JOStuff.EntityFlags.EF_BOUNCE_HALF; // ?!?!

            int knockDownLower = jkaMode ? -2 : 829; // TODO Adapt to 1.04 too? But why, its so different.
            int knockDownUpper = jkaMode ? -2 : 848;

            amNotInSpec = snap.PlayerState.ClientNum == client.clientNum && snap.PlayerState.PlayerMoveType != JKClient.PlayerMoveType.Spectator && snap.PlayerState.PlayerMoveType != JKClient.PlayerMoveType.Intermission; // Some servers (or duel mode) doesn't allow me to go spec. Do funny things then.

            for (int i = 0; i < client.ClientHandler.MaxClients; i++)
            {

                bool oldKnockedDown = infoPool.playerInfo[i].knockedDown;
                
                int snapEntityNum = snapEntityMapping[i];
                if(snapEntityNum == -1 && i == snap.PlayerState.ClientNum)
                {
                    infoPool.playerInfo[i].IsAlive = snap.PlayerState.Stats[0] > 0; // We do this so that if a player respawns but isn't visible, we don't use his (useless) position
                    if (
                        infoPool.playerInfo[i].position.X != snap.PlayerState.Origin[0] ||
                        infoPool.playerInfo[i].position.Y != snap.PlayerState.Origin[1]||
                        infoPool.playerInfo[i].position.Z != snap.PlayerState.Origin[2] ||
                        infoPool.playerInfo[i].angles.X != snap.PlayerState.ViewAngles[0] ||
                        infoPool.playerInfo[i].angles.Y != snap.PlayerState.ViewAngles[1] ||
                        infoPool.playerInfo[i].angles.Z != snap.PlayerState.ViewAngles[2] 
                    )
                    {
                        infoPool.playerInfo[i].lastPositionOrAngleChange = DateTime.Now;
                        lastAnyPositionOrAngleChange = DateTime.Now;
                    }
                    infoPool.playerInfo[i].position.X = snap.PlayerState.Origin[0];
                    infoPool.playerInfo[i].position.Y = snap.PlayerState.Origin[1];
                    infoPool.playerInfo[i].position.Z = snap.PlayerState.Origin[2];
                    infoPool.playerInfo[i].velocity.X = snap.PlayerState.Velocity[0];
                    infoPool.playerInfo[i].velocity.Y = snap.PlayerState.Velocity[1];
                    infoPool.playerInfo[i].velocity.Z = snap.PlayerState.Velocity[2];
                    infoPool.playerInfo[i].angles.X = snap.PlayerState.ViewAngles[0];
                    infoPool.playerInfo[i].angles.Y = snap.PlayerState.ViewAngles[1];
                    infoPool.playerInfo[i].angles.Z = snap.PlayerState.ViewAngles[2];
                    infoPool.playerInfo[i].curWeapon = snap.PlayerState.Weapon;
                    infoPool.playerInfo[i].speed = snap.PlayerState.Speed;
                    infoPool.playerInfo[i].groundEntityNum = snap.PlayerState.GroundEntityNum;
                    infoPool.playerInfo[i].torsoAnim = snap.PlayerState.TorsoAnim;
                    infoPool.playerInfo[i].legsAnim = snap.PlayerState.LegsAnimation;
                    infoPool.playerInfo[i].duelInProgress = snap.PlayerState.DuelInProgress;
                    infoPool.playerInfo[i].saberMove = snap.PlayerState.SaberMove;
                    infoPool.playerInfo[i].forcePowersActive = snap.PlayerState.forceData.ForcePowersActive;
                    this.saberDrawAnimLevel = snap.PlayerState.forceData.SaberDrawAnimLevel;
                    this.baseSpeed = snap.PlayerState.Basespeed;
                    this.delta_angles.X = Short2Angle(snap.PlayerState.DeltaAngles[0]);
                    this.delta_angles.Y = Short2Angle(snap.PlayerState.DeltaAngles[1]);
                    this.delta_angles.Z = Short2Angle(snap.PlayerState.DeltaAngles[2]);

                    infoPool.playerInfo[i].powerUps = 0;
                    for (int y = 0; y < Common.MaxPowerUps; y++)
                    {
                        if (snap.PlayerState.PowerUps[y] > 0)
                        {
                            infoPool.playerInfo[i].powerUps |= 1 << y;
                        }
                    }
                    infoPool.playerInfo[i].lastPositionUpdate = infoPool.playerInfo[i].lastFullPositionUpdate = DateTime.Now;

                    if (((infoPool.playerInfo[i].powerUps & (1 << PWRedFlag)) != 0) && infoPool.playerInfo[i].team != Team.Spectator) // Sometimes stuff seems to glitch and show spectators as having the flag
                    {
                        infoPool.teamInfo[(int)JKClient.Team.Red].lastFlagCarrier = i;
                        infoPool.teamInfo[(int)JKClient.Team.Red].lastFlagCarrierValid = true;
                        infoPool.teamInfo[(int)JKClient.Team.Red].lastFlagCarrierValidUpdate = DateTime.Now;
                        infoPool.teamInfo[(int)JKClient.Team.Red].lastFlagCarrierUpdate = DateTime.Now;
                    }
                    else if (((infoPool.playerInfo[i].powerUps & (1 << PWBlueFlag)) != 0) && infoPool.playerInfo[i].team != Team.Spectator) // Sometimes stuff seems to glitch and show spectators as having the flag
                    {
                        infoPool.teamInfo[(int)JKClient.Team.Blue].lastFlagCarrier = i;
                        infoPool.teamInfo[(int)JKClient.Team.Blue].lastFlagCarrierValid = true;
                        infoPool.teamInfo[(int)JKClient.Team.Blue].lastFlagCarrierValidUpdate = DateTime.Now;
                        infoPool.teamInfo[(int)JKClient.Team.Blue].lastFlagCarrierUpdate = DateTime.Now;
                    }

                    if (SpectatedPlayer.HasValue)
                    {
                        infoPool.lastConfirmedVisible[SpectatedPlayer.Value, i] = DateTime.Now;
                        entityOrPSVisible[i] = true;
                    }
                }
                else if (snapEntityNum != -1 /* entities[i].CurrentValid || entities[i].CurrentFilledFromPlayerState */) {

                    // TODO
                    // This isAlive thing sometimes evaluated wrongly in unpredictable ways. In one instance, it appears it might have 
                    // evaluated to false for a single frame, unless I mistraced the error and this isn't the source of the error at all.
                    // Weird thing is, EntityFlags was not being copied from PlayerState at all! So how come the value changed at all?! It doesn't really make sense.
                    infoPool.playerInfo[i].IsAlive = (snap.Entities[snapEntityNum].EntityFlags & EFDeadFlag) == 0; // We do this so that if a player respawns but isn't visible, we don't use his (useless) position
                    if (
                        infoPool.playerInfo[i].position.X != snap.Entities[snapEntityNum].Position.Base[0] ||
                        infoPool.playerInfo[i].position.Y != snap.Entities[snapEntityNum].Position.Base[1] ||
                        infoPool.playerInfo[i].position.Z != snap.Entities[snapEntityNum].Position.Base[2] ||
                        infoPool.playerInfo[i].velocity.X != snap.Entities[snapEntityNum].Position.Delta[0] ||
                        infoPool.playerInfo[i].velocity.Y != snap.Entities[snapEntityNum].Position.Delta[1] ||
                        infoPool.playerInfo[i].velocity.Z != snap.Entities[snapEntityNum].Position.Delta[2]
                    )
                    {
                        infoPool.playerInfo[i].lastPositionOrAngleChange = DateTime.Now;
                        lastAnyPositionOrAngleChange = DateTime.Now;
                    }
                    infoPool.playerInfo[i].position.X = snap.Entities[snapEntityNum].Position.Base[0];
                    infoPool.playerInfo[i].position.Y = snap.Entities[snapEntityNum].Position.Base[1];
                    infoPool.playerInfo[i].position.Z = snap.Entities[snapEntityNum].Position.Base[2];
                    infoPool.playerInfo[i].velocity.X = snap.Entities[snapEntityNum].Position.Delta[0];
                    infoPool.playerInfo[i].velocity.Y = snap.Entities[snapEntityNum].Position.Delta[1];
                    infoPool.playerInfo[i].velocity.Z = snap.Entities[snapEntityNum].Position.Delta[2];
                    infoPool.playerInfo[i].angles.X = snap.Entities[snapEntityNum].AngularPosition.Base[0];
                    infoPool.playerInfo[i].angles.Y = snap.Entities[snapEntityNum].AngularPosition.Base[1];
                    infoPool.playerInfo[i].angles.Z = snap.Entities[snapEntityNum].AngularPosition.Base[2];
                    infoPool.playerInfo[i].curWeapon = snap.Entities[snapEntityNum].Weapon;
                    infoPool.playerInfo[i].speed = snap.Entities[snapEntityNum].Speed;
                    infoPool.playerInfo[i].groundEntityNum = snap.Entities[snapEntityNum].GroundEntityNum;
                    infoPool.playerInfo[i].torsoAnim = snap.Entities[snapEntityNum].TorsoAnimation;
                    infoPool.playerInfo[i].legsAnim = snap.Entities[snapEntityNum].LegsAnimation;
                    infoPool.playerInfo[i].duelInProgress = snap.Entities[snapEntityNum].Bolt1 == 1;
                    infoPool.playerInfo[i].saberMove = snap.Entities[snapEntityNum].SaberMove;
                    infoPool.playerInfo[i].forcePowersActive = snap.Entities[snapEntityNum].ForcePowersActive;
                    infoPool.playerInfo[i].powerUps = snap.Entities[snapEntityNum].Powerups; // 1/3 places where powerups is transmitted
                    infoPool.playerInfo[i].lastPositionUpdate = infoPool.playerInfo[i].lastFullPositionUpdate = DateTime.Now;
                    
                    if(((infoPool.playerInfo[i].powerUps & (1 << PWRedFlag)) != 0) && infoPool.playerInfo[i].team != Team.Spectator) // Sometimes stuff seems to glitch and show spectators as having the flag
                    {
                        infoPool.teamInfo[(int)JKClient.Team.Red].lastFlagCarrier = i;
                        infoPool.teamInfo[(int)JKClient.Team.Red].lastFlagCarrierValid = true;
                        infoPool.teamInfo[(int)JKClient.Team.Red].lastFlagCarrierValidUpdate = DateTime.Now;
                        infoPool.teamInfo[(int)JKClient.Team.Red].lastFlagCarrierUpdate = DateTime.Now;
                    } else if (((infoPool.playerInfo[i].powerUps & (1 << PWBlueFlag)) != 0) && infoPool.playerInfo[i].team != Team.Spectator) // Sometimes stuff seems to glitch and show spectators as having the flag
                    {
                        infoPool.teamInfo[(int)JKClient.Team.Blue].lastFlagCarrier = i;
                        infoPool.teamInfo[(int)JKClient.Team.Blue].lastFlagCarrierValid = true;
                        infoPool.teamInfo[(int)JKClient.Team.Blue].lastFlagCarrierValidUpdate = DateTime.Now;
                        infoPool.teamInfo[(int)JKClient.Team.Blue].lastFlagCarrierUpdate = DateTime.Now;
                    }

                    if (SpectatedPlayer.HasValue)
                    {
                        infoPool.lastConfirmedVisible[SpectatedPlayer.Value, i] = DateTime.Now;
                        entityOrPSVisible[i] = true;
                    }
                } else
                {
                    if (SpectatedPlayer.HasValue)
                    {
                        infoPool.lastConfirmedInvisible[SpectatedPlayer.Value, i] = DateTime.Now;
                        entityOrPSVisible[i] = false;
                    }
                }

                int currentLegsAnim = infoPool.playerInfo[i].legsAnim & ~2048;
                int currentTorsoAnim = infoPool.playerInfo[i].torsoAnim & ~2048;
                infoPool.playerInfo[i].knockedDown = (currentLegsAnim >= knockDownLower && currentLegsAnim <= knockDownUpper) || (currentTorsoAnim >= knockDownLower && currentTorsoAnim <= knockDownUpper);

                if (infoPool.playerInfo[i].knockedDown && !oldKnockedDown)
                {
                    infoPool.playerInfo[i].chatCommandTrackingStuff.falls++;
                }
            }

            PlayerState currentPs = snap.PlayerState;
            if(currentPs.ClientNum >= 0 && currentPs.ClientNum < client.ClientHandler.MaxClients) // Dunno why it shouldnt be but i dont want any crashes.
            {
                // Update the followed player's score in realtime.
                infoPool.playerInfo[currentPs.ClientNum].score.score = currentPs.Persistant[(int)PersistantEnum.PERS_SCORE];
            }

            // Save flag positions. 
            // Each flag (red & blue) has a base entity and a dropped entity (if dropped and not yet returned)
            // We want to save each for extra info. 
            // A currently picked up/carried/dropped flag's base entity is not sent to clients, but it still exists
            // as a separate game entity from the dropped flag. 
            // How can we tell if a flag entity is a dropped or a base flag?
            // The game entity has the FL_DROPPED_ITEM flag added, however this doesn't appear to be shared with the clients.
            // We can try and get some clues from other stuff though.
            // Dropped flags get an EF_BOUNCE_HALF eFlag (eFlag is different from flag, latter is server only). 
            // Dropped flags also have a pos.trType of TR_GRAVITY.
            // However pos.trType actually gets reset to TR_STATIONARY once the bouncing is over.
            // The bounce flag might however remain, I do not see it being deleted, so that's a way to go?
            // 
            // In theory we should be able to just check the flag item against the flag status (cs 23) but 
            // we might be (?) at a point in time where the new flag status has not yet been parsed, but the new 
            // entities have, so we might mistake a base flag for a dropped one or vice versa.
            for (int i = client.ClientHandler.MaxClients; i < JKClient.Common.MaxGEntities; i++)
            {
                int snapEntityNum = snapEntityMapping[i];
                if (snapEntityNum != -1/*entities[i].CurrentValid*/)
                {
                    if (SpectatedPlayer.HasValue)
                    {
                        infoPool.lastConfirmedVisible[SpectatedPlayer.Value, i] = DateTime.Now;
                        entityOrPSVisible[i] = true;
                    }

                    // Flag bases
                    if (snap.Entities[snapEntityNum].EntityType == ETTeam)
                    {
                        Team team = (Team)snap.Entities[snapEntityNum].ModelIndex;
                        if (team == Team.Blue || team == Team.Red)
                        {
                            infoPool.teamInfo[(int)team].flagBasePosition.X = snap.Entities[snapEntityNum].Position.Base[0];
                            infoPool.teamInfo[(int)team].flagBasePosition.Y = snap.Entities[snapEntityNum].Position.Base[1];
                            infoPool.teamInfo[(int)team].flagBasePosition.Z = snap.Entities[snapEntityNum].Position.Base[2];
                            infoPool.teamInfo[(int)team].flagBaseEntityNumber = i;
                            infoPool.teamInfo[(int)team].lastFlagBasePositionUpdate = DateTime.Now;
                        }
                    } else if (snap.Entities[snapEntityNum].EntityType == ETItem)
                    {
                        if(snap.Entities[snapEntityNum].ModelIndex == infoPool.teamInfo[(int)Team.Red].flagItemNumber ||
                            snap.Entities[snapEntityNum].ModelIndex == infoPool.teamInfo[(int)Team.Blue].flagItemNumber
                            )
                        {

                            Team team = snap.Entities[snapEntityNum].ModelIndex == infoPool.teamInfo[(int)Team.Red].flagItemNumber ? Team.Red : Team.Blue;

                            // Check if it's base flag item or dropped one
                            if ((snap.Entities[snapEntityNum].EntityFlags & (int)JOStuff.EntityFlags.EF_BOUNCE_HALF) != 0 || (jkaMode && infoPool.teamInfo[(int)team].flag == FlagStatus.FLAG_DROPPED)) // This is DIRTY.
                            {
                                // This very likely is a dropped flag, as dropped flags get the EF_BOUNCE_HALF entity flag.
                                infoPool.teamInfo[(int)team].flagDroppedPosition.X = snap.Entities[snapEntityNum].Position.Base[0];
                                infoPool.teamInfo[(int)team].flagDroppedPosition.Y = snap.Entities[snapEntityNum].Position.Base[1];
                                infoPool.teamInfo[(int)team].flagDroppedPosition.Z = snap.Entities[snapEntityNum].Position.Base[2];
                                infoPool.teamInfo[(int)team].droppedFlagEntityNumber = i;
                                infoPool.teamInfo[(int)team].lastFlagDroppedPositionUpdate = DateTime.Now;

                            } else if (!jkaMode || infoPool.teamInfo[(int)team].flag == FlagStatus.FLAG_ATBASE) // This is DIRTY. I hate it. Timing could mess this up. Hmm or maybe not? Configstrings are handled first. Hmm. Well it's the best I can do for JKA.
                            {
                                // This very likely is a base flag item, as it doesn't have an EF_BOUNCE_HALF entity flag.
                                infoPool.teamInfo[(int)team].flagBaseItemPosition.X = snap.Entities[snapEntityNum].Position.Base[0];
                                infoPool.teamInfo[(int)team].flagBaseItemPosition.Y = snap.Entities[snapEntityNum].Position.Base[1];
                                infoPool.teamInfo[(int)team].flagBaseItemPosition.Z = snap.Entities[snapEntityNum].Position.Base[2];
                                infoPool.teamInfo[(int)team].flagBaseItemEntityNumber = i;
                                infoPool.teamInfo[(int)team].lastFlagBaseItemPositionUpdate = DateTime.Now;
                                
                            }
                        }
                    }
                }
                else
                {
                    if (SpectatedPlayer.HasValue)
                    {
                        infoPool.lastConfirmedInvisible[SpectatedPlayer.Value, i] = DateTime.Now;
                        entityOrPSVisible[i] = false;
                    }
                }
            }

            bool isSillyCameraOperator = this.CameraOperator.HasValue && serverWindow.getCameraOperatorOfConnection(this) is CameraOperators.SillyCameraOperator;
            bool maySendFollow = (!isSillyCameraOperator || !amNotInSpec) && (!amNotInSpec || !isDuelMode || jkaMode) && snap.PlayerState.PlayerMoveType != JKClient.PlayerMoveType.Intermission; // In jk2, sending follow while it being ur turn in duel will put you back in spec but fuck up the whole game for everyone as it is always your turn then.
            if (amNotInSpec) // Maybe in the future I will
            {
                
                if (!isSillyCameraOperator) // Silly operator means we actually don't want to be in spec. That is its only purpose.
                {
                    // TODO: Special handling for selfkill when g_allowduelSuicide 1?
                    // TODO: Why does it fuck up the order in jk2?

                    // Try to get back out of spec
                    // Depending on server settings, this might not work though, but hey, we can try.

                    // Duel could theoretically allow suicide, in which case this could be used to let next player play safely.
                    leakyBucketRequester.requestExecution("kill", RequestCategory.SELFKILL, 5, 10000, LeakyBucketRequester<string, RequestCategory>.RequestBehavior.DISCARD_IF_ONE_OF_TYPE_ALREADY_EXISTS);

                    if (maySendFollow) // In jka i can actually weasel out of duels like this, but not in jk2 sadly. It puts me spec but immediately queues me back up for the next fight. Sad. Never found out why.
                    {
                        leakyBucketRequester.requestExecution("team spectator", RequestCategory.GOINTOSPEC, 5, 6000, LeakyBucketRequester<string, RequestCategory>.RequestBehavior.DISCARD_IF_ONE_OF_TYPE_ALREADY_EXISTS);
                    
                        // Ironic... this does allow me to go spec in duel, but it creates an endless loop where I am always the next upcoming player, at leasts in jk2. :( Even worse
                        leakyBucketRequester.requestExecution("follownext", RequestCategory.GOINTOSPECHACK, 5, 3000, LeakyBucketRequester<string, RequestCategory>.RequestBehavior.DISCARD_IF_ONE_OF_TYPE_ALREADY_EXISTS); // Evil hack to go spec in duel mode LOL
                    }
                }
            }
            else
            {
                // Can't reproduce it but once ended up with a weird endless loop of going spec and following. Musts be very unlucky timing combined with some weird shit.
                leakyBucketRequester.purgeByKinds( new RequestCategory[] { RequestCategory.SELFKILL, RequestCategory.GOINTOSPEC, RequestCategory.GOINTOSPECHACK});
            }

            bool spectatedPlayerIsBot = SpectatedPlayer.HasValue && playerIsLikelyBot(SpectatedPlayer.Value);
            bool spectatedPlayerIsVeryAfk = SpectatedPlayer.HasValue && playerIsVeryAfk(SpectatedPlayer.Value,true);
            bool onlyBotsActive = (infoPool.lastBotOnlyConfirmed.HasValue && (DateTime.Now - infoPool.lastBotOnlyConfirmed.Value).TotalMilliseconds < 10000) || infoPool.botOnlyGuaranteed;
            if (maySendFollow && AlwaysFollowSomeone && infoPool.lastScoreboardReceived != null && (ClientNum == SpectatedPlayer || (!this.CameraOperator.HasValue && ((spectatedPlayerIsBot && !onlyBotsActive)|| spectatedPlayerIsVeryAfk)))) // Not following anyone. Let's follow someone.
            {
                int highestScore = int.MinValue;
                int highestScorePlayer = -1;
                // Pick player with highest score.
findHighestScore:
                foreach (PlayerInfo player in infoPool.playerInfo)
                {
                    if ((DateTime.Now-clientsWhoDontWantTOrCannotoBeSpectated[player.clientNum]).TotalMilliseconds > 120000 && player.infoValid && player.team != Team.Spectator && (player.score.score > highestScore || highestScorePlayer == -1) && (onlyBotsActive || !playerIsLikelyBot(player.clientNum)) && (!playerIsVeryAfk(player.clientNum,false)|| (spectatedPlayerIsVeryAfk && player.clientNum != SpectatedPlayer)))
                    {
                        highestScore = player.score.score;
                        highestScorePlayer = player.clientNum;
                    }
                }
                if (highestScorePlayer != -1) // Assuming any players at all exist that are playing atm.
                {
                    lastRequestedAlwaysFollowSpecClientNum = highestScorePlayer;
                    leakyBucketRequester.requestExecution("follow " + highestScorePlayer, RequestCategory.FOLLOW, 1, 2000, LeakyBucketRequester<string, RequestCategory>.RequestBehavior.DISCARD_IF_ONE_OF_TYPE_ALREADY_EXISTS);
                }
            }
        }

        private bool playerIsLikelyBot(int clientNumber)
        {
            return clientNumber >= 0 && clientNumber < client.ClientHandler.MaxClients && (infoPool.playerInfo[clientNumber].confirmedBot || !infoPool.playerInfo[clientNumber].score.lastNonZeroPing.HasValue || (DateTime.Now - infoPool.playerInfo[clientNumber].score.lastNonZeroPing.Value).TotalMilliseconds > 10000) && infoPool.playerInfo[clientNumber].score.pingUpdatesSinceLastNonZeroPing > 10;
        }
        private bool playerIsVeryAfk(int clientNumber, bool followed = false)
        {
            return clientNumber >= 0 && clientNumber < client.ClientHandler.MaxClients && 
                (
                (infoPool.playerInfo[clientNumber].lastPositionOrAngleChange.HasValue && (DateTime.Now-infoPool.playerInfo[clientNumber].lastPositionOrAngleChange.Value).TotalMinutes > 60)
                || (followed && lastAnyPositionOrAngleChange.HasValue && (DateTime.Now-lastAnyPositionOrAngleChange.Value).TotalMinutes > 5)
                );
        }

        private void OnServerInfoChanged(ServerInfo obj)
        {
            this.ServerInfoChanged?.Invoke(obj);
        }

        private string oldMapName = "";
        PathFinder pathFinder = null;

        // Update player list
        private void Connection_ServerInfoChanged(ServerInfo obj)
        {
            OnServerInfoChanged(obj);
            //obj.GameName
            if (/*obj.GameName.Contains("JA+ Mod", StringComparison.OrdinalIgnoreCase) || */obj.GameName.Contains("JA+", StringComparison.OrdinalIgnoreCase) || obj.GameName.Contains("^5X^2Jedi ^5Academy", StringComparison.OrdinalIgnoreCase) || obj.GameName.Contains("^4U^3A^5Galaxy", StringComparison.OrdinalIgnoreCase) || obj.GameName.Contains("AbyssMod", StringComparison.OrdinalIgnoreCase))
            {
                this.JAPlusDetected = true;
            }
            else if(obj.GameName.Contains("japro", StringComparison.OrdinalIgnoreCase))
            {
                this.JAProDetected = true;
            } else if(obj.GameName.Contains("Movie Battles II", StringComparison.OrdinalIgnoreCase))
            {
                if (!this.MBIIDetected)
                {
                    infoPool.ResetFlagItemNumbers(true);
                }
                this.MBIIDetected = true;
            }
            infoPool.lastBotOnlyConfirmed = null; // Because if a new player just entered, we have no idea if it's only a bot or  not until we get his ping via score command.
            string serverName = client.ServerInfo.HostName;
            if (serverName != "")
            {
                serverWindow.ServerName = obj.HostName;
            }

            if(obj.MapName != oldMapName)
            {
                pathFinder = BotRouteManager.GetPathFinder(obj.MapName);
                oldMapName = obj.MapName;
            }

            currentGameType = obj.GameType;
            isDuelMode = obj.GameType == GameType.Duel || obj.GameType == GameType.PowerDuel;

            // Check for referencedPaks
            InfoString systemInfo = new InfoString( client.GetMappedConfigstring(ClientGame.Configstring.SystemInfo));
            if(systemInfo["sv_referencedPakNames"] != lastKnownPakNames || systemInfo["sv_referencedPaks"] != lastKnownPakChecksums)
            {
                // Referenced paks changed:
                lastKnownPakNames = systemInfo["sv_referencedPakNames"];
                lastKnownPakChecksums = systemInfo["sv_referencedPaks"];

                string lastKnownPakNamesCaptured = lastKnownPakNames; // Capture for parallel thread
                string lastKnownPakChecksumsCaptured = lastKnownPakChecksums;
                if(mvHttpDownloadInfo == null || mvHttpDownloadInfo.Value.httpIsAvailable){
                    serverWindow.addToLog("Systeminfo: Referenced paks changed, trying to save to download list.");
                    Task.Run(async () => {
                        string[] pakNames = lastKnownPakNamesCaptured.Trim(' ').Split(" ");
                        string[] pakChecksums = lastKnownPakChecksumsCaptured.Trim(' ').Split(" ");
                        if(pakNames.Length != pakChecksums.Length)
                        {
                            serverWindow.addToLog("WARNING: Amount of pak names does not match amount of pak checksums. Weird. Aborting pak name logging this time.");
                            return;
                        } else if (pakNames.Length == 0)
                        {
                            serverWindow.addToLog("Referenced paks count is 0.");
                            return;
                        }

                        if(mvHttpDownloadInfo == null)
                        {
                            // Let's get server info packet.
                            using (ServerBrowser browser = new ServerBrowser(new JKClient.JOBrowserHandler(obj.Protocol)) { ForceStatus = true })
                            {
                                browser.Start(async (JKClientException ex)=> {
                                    serverWindow.addToLog("Exception trying to get ServerInfo for mvHttp purposes: "+ex.ToString());
                                });
                                NetAddress serverAddress = NetAddress.FromString(ip);

                                InfoString infoString = null;
                                try { 
                                    infoString = await browser.GetServerInfoInfo(serverAddress);
                                }
                                catch (Exception e)
                                {
                                    serverWindow.addToLog("Exception trying to get ServerInfo for mvHttp purposes (during await): " + e.ToString());
                                    return;
                                }

                                MvHttpDownloadInfo tmpDLInfo = new MvHttpDownloadInfo();
                                tmpDLInfo.httpIsAvailable = false;
                                if (infoString.ContainsKey("mvhttp"))
                                {
                                    string serverAddressString = serverAddress.ToString();
                                    string[] serverAddressStringParts = serverAddressString.Split(":");
                                    if(serverAddressStringParts.Length > 0)
                                    {
                                        tmpDLInfo.httpIsAvailable = true;
                                        tmpDLInfo.urlPrefix = "http://" + serverAddressStringParts[0] + ":" + infoString["mvhttp"] + "/";
                                        serverWindow.addToLog($"Http downloads possible via port: {tmpDLInfo.urlPrefix}");
                                    }
                                } else if(infoString.ContainsKey("mvhttpurl"))
                                {
                                    tmpDLInfo.httpIsAvailable = true;
                                    tmpDLInfo.urlPrefix = infoString["mvhttpurl"];
                                    serverWindow.addToLog($"Http downloads possible via possibly external url: {tmpDLInfo.urlPrefix}");
                                } else
                                {
                                    tmpDLInfo.httpIsAvailable = false; 
                                    serverWindow.addToLog("Http downloads not available.");
                                }

                                mvHttpDownloadInfo = tmpDLInfo;

                                browser.Stop();
                            }
                        }

                        if(mvHttpDownloadInfo != null && mvHttpDownloadInfo.Value.httpIsAvailable)
                        {

                            List<string> downloadLinks = new List<string>();
                            for(int pkI = 0; pkI<pakNames.Length; pkI++)
                            {
                                string pakName = pakNames[pkI];
                                string pakChecksum = pakChecksums[pkI];
                                int pakChecksumInt;
                                if (int.TryParse(pakChecksum, out pakChecksumInt))
                                {
                                    string dlLink = mvHttpDownloadInfo.Value.urlPrefix + pakName + ".pk3";
                                    string hashString = Convert.ToHexString(BitConverter.GetBytes(pakChecksumInt));
                                    serverWindow.addToLog($"Logged pk3 download url: {dlLink}");
                                    downloadLinks.Add($"{pakName},{hashString},{dlLink}");
                                    PakDownloader.Enqueue(dlLink, pakChecksumInt);
                                } else
                                {
                                    serverWindow.addToLog("Could not parse checksum integer, strange. Discarding.");
                                }
                            }
                            string[] downloadLinksArray = downloadLinks.ToArray();
                            Dispatcher.CurrentDispatcher.Invoke(()=> {
                                Helpers.logDownloadLinks(downloadLinksArray);
                            });
                        }


                    });
                } else
                {
                    serverWindow.addToLog("Systeminfo: Referenced paks changed, but http downloads are disabled.");
                }
            }

            ClientNum = client.clientNum;
            SpectatedPlayer = client.playerStateClientNum;

            infoPool.MapName = client.ServerInfo.MapName;
            infoPool.teamInfo[(int)Team.Red].teamScore = client.GetMappedConfigstring(ClientGame.Configstring.Scores1).Atoi();
            infoPool.ScoreRed = client.GetMappedConfigstring(ClientGame.Configstring.Scores1).Atoi();
            infoPool.teamInfo[(int)Team.Blue].teamScore = client.GetMappedConfigstring(ClientGame.Configstring.Scores2).Atoi();
            infoPool.ScoreBlue = client.GetMappedConfigstring(ClientGame.Configstring.Scores2).Atoi();

            if (obj.FloodProtect >=-1)
            {
                // Don't trust a setting of 0. Could still have non-engine limiting that isn't captured by the sv_floodprotect cvar.
                // In short, assume the worst: only 1 per second.
                int burst = obj.FloodProtect > 0 ? obj.FloodProtect : 1; // 0 means flood protection is disabled. Let's still try to be somewhat gracious and just set burst to 10
                leakyBucketRequester.changeParameters(burst, floodProtectPeriod);
            } else if (obj.FloodProtect == -2)
            {
                // This server has not sent an sv_floodprotect variable. Might be a legacy server without the leaky bucket algo
                // Be safe and set burst to 1, or risk losing commands
                leakyBucketRequester.changeParameters(1, floodProtectPeriod);
            }

            if(client.ClientInfo == null)
            {
                return;
            }
            bool noActivePlayers = true;
            bool anyNonBotActivePlayers = false;
            lock (infoPoolResetStuffMutex) { // Try to make sure various connections don't get in conflict here since we are doing some resetting by comparing previouss and new values.
                for(int i = 0; i < client.ClientHandler.MaxClients; i++)
                {
                    if(client.ClientInfo[i].Team != Team.Spectator && client.ClientInfo[i].InfoValid)
                    {
                        noActivePlayers = false;
                    }

                    infoPool.playerInfo[i].team = client.ClientInfo[i].Team;

                    // Whole JkWatcher instance based
                    if (infoPool.playerInfo[i].infoValid != client.ClientInfo[i].InfoValid) { 
                        // Client connected/disconnected. Reset some stats
                        for(int p=0;p< client.ClientHandler.MaxClients; p++)
                        {
                            infoPool.killTrackers[i, p] = new KillTracker();
                            infoPool.killTrackers[p, i] = new KillTracker();
                        }
                        infoPool.playerInfo[i].chatCommandTrackingStuff = new ChatCommandTrackingStuff() { onlineSince=DateTime.Now};
                    }

                    // Connection based
                    if (clientInfoValid[i] != client.ClientInfo[i].InfoValid) { 
                        this.demoRateLimiters[i] = new DemoRequestRateLimiter(); // Not part of infopool because its unique to each connection.
                    }

                    if (client.ClientInfo[i].InfoValid && infoPool.playerInfo[i].name != client.ClientInfo[i].Name)
                    {
                        if (CheckPlayerBlacklist(client.ClientInfo[i].Name))
                        {
                            infoPool.playerInfo[i].chatCommandTrackingStuff.fightBotBlacklist = true;
                        }
                    }

                    infoPool.playerInfo[i].name = client.ClientInfo[i].Name;

                    clientInfoValid[i] = client.ClientInfo[i].InfoValid;
                    infoPool.playerInfo[i].infoValid = client.ClientInfo[i].InfoValid;
                    infoPool.playerInfo[i].clientNum = client.ClientInfo[i].ClientNum;
                    infoPool.playerInfo[i].confirmedBot = client.ClientInfo[i].BotSkill > -0.5f; // Checking for -1 basically but it's float so be safe.

                    if (!infoPool.playerInfo[i].confirmedBot && infoPool.playerInfo[i].team != Team.Spectator && infoPool.playerInfo[i].infoValid)
                    {
                        anyNonBotActivePlayers = true;
                    }

                    infoPool.playerInfo[i].lastClientInfoUpdate = DateTime.Now;
                }
            }
            infoPool.botOnlyGuaranteed = !anyNonBotActivePlayers;
            infoPool.NoActivePlayers = noActivePlayers;
            serverWindow.Dispatcher.Invoke(() => {
                lock (serverWindow.playerListDataGrid)
                {
                    serverWindow.playerListDataGrid.ItemsSource = null;
                    serverWindow.playerListDataGrid.ItemsSource = infoPool.playerInfo;
                }
            });


            // Any reason to have this here when it's already in snapshotparsed?
            // The one in snapshotparsed is also more advanced and does more cool stuff like check for bots
            /*if (AlwaysFollowSomeone && ClientNum == SpectatedPlayer) // Not following anyone. Let's follow someone.
            {
                int highestScore = int.MinValue;
                int highestScorePlayer = -1;
                // Pick player with highest score.
                foreach(PlayerInfo player in infoPool.playerInfo)
                {
                    if((DateTime.Now - clientsWhoDontWantToBeSpectated[player.clientNum]).TotalMilliseconds > 120000 && player.infoValid && player.team != Team.Spectator && (player.score.score > highestScore || highestScorePlayer == -1))
                    {
                        highestScore = player.score.score;
                        highestScorePlayer = player.clientNum;
                    }
                }
                if(highestScorePlayer != -1) // Assuming any players at all exist that are playing atm.
                {
                    lastRequestedAlwaysFollowSpecClientNum = highestScorePlayer;
                    leakyBucketRequester.requestExecution("follow " + highestScorePlayer, RequestCategory.FOLLOW, 1, 2000, LeakyBucketRequester<string, RequestCategory>.RequestBehavior.DISCARD_IF_ONE_OF_TYPE_ALREADY_EXISTS);
                }
            }*/

            snapsEnforcementUpdate();
        }

        public void disconnect()
        {
            // In very very rare cases (some bug?) a weird disconnect can happen
            // And it thinks demo is still recording or sth? So just be clean.
            client.StopRecord_f();
            updateName();
            isRecordingADemo = false;

            client.Disconnected -= Client_Disconnected; // This only handles involuntary disconnects
            Client oldClientForHandler = client; // Since maybe we reconnect straight after this.
            client.Disconnected += (a,b)=> { // Replace handler that auto-reconnects with handler that disposes of client.
                oldClientForHandler.Stop();
                oldClientForHandler.Dispose();
                oldClientForHandler.StopRecord_f();
                try // I'm putting in a try because maybe at this point we are destroying the Connection object so it might not exist anymore later and lead to errors?
                {
                    updateName();
                } catch(Exception e)
                {
                    try
                    {
                        serverWindow.addToLog("Error updating name after disconnect, coulda seen it coming I guess. "+e.ToString(),true);
                    } catch(Exception e2)
                    {
                        // Eh whatever.
                    }
                }
                serverWindow.addToLog("Disconnected.");
            };
            client.Disconnect();
            trulyDisconnected = true;
            client.ServerCommandExecuted -= ServerCommandExecuted;
            client.ServerInfoChanged -= Connection_ServerInfoChanged;
            client.SnapshotParsed -= Client_SnapshotParsed;
            client.EntityEvent -= Client_EntityEvent;
            client.UserCommandGenerated -= Client_UserCommandGenerated;
            client.DebugEventHappened -= Client_DebugEventHappened;
            clientStatistics = null;
        }

        

        List<string> serverCommandsVerbosityLevel0WhiteList = new List<string>() {"chat","tchat","lchat","print","cp","disconnect" };
        List<string> serverCommandsVerbosityLevel2WhiteList = new List<string>() {"chat","tchat","lchat","print","cp","disconnect","cs" };
        List<string> serverCommandsVerbosityLevel4BlackList = new List<string>() {"scores","tinfo", "newDefered", "pstats" };

        void ServerCommandExecuted(CommandEventArgs commandEventArgs)
        {
            string command = commandEventArgs.Command.Argv(0);

            switch (command)
            {
                case "tinfo":
                    EvaluateTInfo(commandEventArgs);
                    break;
                case "scores":
                    EvaluateScore(commandEventArgs);
                    break;
                case "cs":
                    EvaluateCS(commandEventArgs);
                    break;
                case "fprint":
                case "print":
                    EvaluatePrint(commandEventArgs);
                    break;
                case "chat":
                case "tchat":
                    EvaluateChat(commandEventArgs);
                    break;
                default:
                    break;
            }

            if (serverWindow.verboseOutput ==5 || 
                (serverWindow.verboseOutput >=4 && !serverCommandsVerbosityLevel4BlackList.Contains(commandEventArgs.Command.Argv(0))) || 
                (serverWindow.verboseOutput >=2 && serverCommandsVerbosityLevel2WhiteList.Contains(commandEventArgs.Command.Argv(0))) || 
                (serverWindow.verboseOutput ==0 && serverCommandsVerbosityLevel0WhiteList.Contains(commandEventArgs.Command.Argv(0)))
                ) { 
                StringBuilder allArgs = new StringBuilder();
                for (int i = 0; i < commandEventArgs.Command.Argc; i++)
                {
                    allArgs.Append(commandEventArgs.Command.Argv(i));
                    allArgs.Append(" ");
                }
                serverWindow.addToLog(allArgs.ToString());
            }
            //addToLog(commandEventArgs.Command.Argv(0)+" "+ commandEventArgs.Command.Argv(1)+" "+ commandEventArgs.Command.Argv(2)+" "+ commandEventArgs.Command.Argv(3));
            Debug.WriteLine(commandEventArgs.Command.Argv(0));
        }

        void EvaluateFlagStatus(string str)
        {
            if (client.ServerInfo.GameType == GameType.CTF || client.ServerInfo.GameType == GameType.CTY)
            {
                // format is rb where its red/blue, 0 is at base, 1 is taken, 2 is dropped
                if (str.Length < 2)
                {
                    // This happens sometimes, for example on NWH servers between 2 games
                    // Server will send cs 23 0 and cs 23 00 in succession, dunno why.
                    // The first one with the single zero is the obvious problem.
                    serverWindow.addToLog("Configstring weirdness, cs 23 had parameter " + str + "(Length " + str.Length + ")");
                    if (str.Length == 1 && str == "0")
                    {
                        infoPool.teamInfo[(int)Team.Red].flag = 0;
                        infoPool.teamInfo[(int)Team.Red].lastFlagUpdate = DateTime.Now;
                        infoPool.teamInfo[(int)Team.Blue].flag = 0;
                        infoPool.teamInfo[(int)Team.Blue].lastFlagUpdate = DateTime.Now;
                    }
                }
                else
                {
                    // If it was picked up or generally status changed, and it was at base before, remember this as the last time it was at base.
                    foreach (int teamToCheck in Enum.GetValues(typeof(Team)))
                    {
                        if (infoPool.teamInfo[teamToCheck].flag == FlagStatus.FLAG_ATBASE)
                        {
                            infoPool.teamInfo[teamToCheck].lastTimeFlagWasSeenAtBase = DateTime.Now;
                        }
                    }
                    int tmp = int.Parse(str[0].ToString());
                    infoPool.teamInfo[(int)Team.Red].flag = tmp == 2 ? FlagStatus.FLAG_DROPPED : (FlagStatus)tmp;
                    infoPool.teamInfo[(int)Team.Red].lastFlagUpdate = DateTime.Now;
                    tmp = int.Parse(str[1].ToString());
                    infoPool.teamInfo[(int)Team.Blue].flag = tmp == 2 ? FlagStatus.FLAG_DROPPED : (FlagStatus)tmp;
                    infoPool.teamInfo[(int)Team.Blue].lastFlagUpdate = DateTime.Now;
                }

                // Reasoning: If a flag is dropped/in base, and then it is taken and our cam operator knows this, but the current flag carrier isn't updated *yet*, we will otherwise assume the wrong flag carrier.
                // So assume the lastflagcarrier info invalid until it is actually set again anew.
                if (infoPool.teamInfo[(int)Team.Red].flag != FlagStatus.FLAG_TAKEN)
                {
                    infoPool.teamInfo[(int)Team.Red].lastFlagCarrierValid = false;
                    infoPool.teamInfo[(int)Team.Red].lastFlagCarrierValidUpdate = DateTime.Now;
                }
                if (infoPool.teamInfo[(int)Team.Blue].flag != FlagStatus.FLAG_TAKEN)
                {
                    infoPool.teamInfo[(int)Team.Blue].lastFlagCarrierValid = false;
                    infoPool.teamInfo[(int)Team.Blue].lastFlagCarrierValidUpdate = DateTime.Now;
                }
                /*infoPool.redFlag = str[0] - '0';
                infoPool.blueFlag = str[1] - '0';
                if (cgs.isCTFMod && cgs.CTF3ModeActive)
                    cgs.yellowflag = str[2] - '0';
                else
                    cgs.yellowflag = 0;*/
            }
        }

        void EvaluateCS(CommandEventArgs commandEventArgs)
        {
            int num = commandEventArgs.Command.Argv(1).Atoi();

            switch (num)
            {
                case (int)ConfigStringDefines.CS_FLAGSTATUS:
                    EvaluateFlagStatus(commandEventArgs.Command.Argv(2));
                    break;
                default:break;
            }
        }

        public bool ConnectionLimitReached { get; private set; } = false;

        private DateTime lastClientDoesNotWishToBeSpectated = DateTime.Now.AddYears(-1);

        // Parse specs sent through NWH "specs" command.
        // [1] = clientNumSpectator
        // [2] = nameSpectator (with trailing whitespaces)
        // [3] = nameSpectated (with trailing whitespaces)
        // [4] = clientNumSpectated
        Regex specsRegex = new Regex(@"^(\d+)\^3\) \^7 (.*?)\^7   (.*?)\((\d+)\)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
        bool skipSanityCheck = false;

        Regex unknownCmdRegex = new Regex(@"^unknown (?:cmd|command) ([^\n]+?)\n\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
        Regex clientInactiveRegex = new Regex(@"Client '?(\d+)'? is not active", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

        void EvaluatePrint(CommandEventArgs commandEventArgs)
        {
            Match specMatch;
            Match unknownCmdMatch;
            if (commandEventArgs.Command.Argc >= 2)
            {
                if((commandEventArgs.Command.Argv(1).Contains("^7Error^1:^7 The client is currently on another map^1.^7") || commandEventArgs.Command.Argv(1).Contains("^7Error^1:^7 The client does not wish to be spectated^1.^7")) && commandEventArgs.Command.Argv(0) == "print")
                {
                    if(lastRequestedAlwaysFollowSpecClientNum >= 0 && lastRequestedAlwaysFollowSpecClientNum<32)
                    {
                        clientsWhoDontWantTOrCannotoBeSpectated[lastRequestedAlwaysFollowSpecClientNum] = DateTime.Now;
                    }
                    lastClientDoesNotWishToBeSpectated = DateTime.Now;
                } else if(commandEventArgs.Command.Argv(1) == "Connection limit reached.\n" || (commandEventArgs.Command.Argv(1).Contains("Too many connections from the same IP.") && commandEventArgs.Command.Argv(0) == "print"))
                {
                    ConnectionLimitReached = true;
                } else if ((specMatch = specsRegex.Match(commandEventArgs.Command.Argv(1))).Success)
                {
                    // Is this info about who is spectating who?
                    if (specMatch.Groups.Count < 5) return;

                    int spectatingPlayer = -1;
                    int spectatedPlayer = -1;
                    int.TryParse(specMatch.Groups[1].Value, out spectatingPlayer);
                    int.TryParse(specMatch.Groups[4].Value, out spectatedPlayer);

                    //if(spectatingPlayer != -1 && spectatedPlayer != -1)
                    if(spectatingPlayer >= 0 && spectatingPlayer < 32 && spectatedPlayer >= 0 && spectatedPlayer < 32)
                    {
                        // Do sanity check that names match
                        // The regex match has trailing spaces so we just take a substring
                        string spectatingActualName = infoPool.playerInfo[spectatingPlayer].name;
                        string spectatingRegexName = specMatch.Groups[2].Value;
                        string spectatedActualName = infoPool.playerInfo[spectatedPlayer].name;
                        string spectatedRegexName = specMatch.Groups[3].Value;

                        int spectatingStringMaxLength = Math.Min(spectatingActualName.Length, spectatingRegexName.Length);
                        int spectatedStringMaxLength = Math.Min(spectatedActualName.Length, spectatedRegexName.Length);

                        spectatingActualName = spectatingActualName.Substring(0, spectatingStringMaxLength);
                        spectatingRegexName = spectatingRegexName.Substring(0, spectatingStringMaxLength);
                        spectatedActualName = spectatedActualName.Substring(0, spectatedStringMaxLength);
                        spectatedRegexName = spectatedRegexName.Substring(0, spectatedStringMaxLength);

                        if (skipSanityCheck || (spectatingActualName == spectatingRegexName && spectatedActualName == spectatedRegexName && spectatingStringMaxLength > 0 && spectatedStringMaxLength > 0))
                        {
                            infoPool.playerInfo[spectatingPlayer].nwhSpectatedPlayer = spectatedPlayer;
                            infoPool.playerInfo[spectatingPlayer].nwhSpectatedPlayerLastUpdate = DateTime.Now;
                        }
                    }
                } if ((specMatch = clientInactiveRegex.Match(commandEventArgs.Command.Argv(1))).Success)
                {
                    // Is this info about who is spectating who?
                    if (specMatch.Groups.Count < 2) return;

                    int inactiveClientNum = -1;
                    int.TryParse(specMatch.Groups[1].Value, out inactiveClientNum);

                    if(inactiveClientNum >= 0 && inactiveClientNum < 32)
                    {
                        clientsWhoDontWantTOrCannotoBeSpectated[inactiveClientNum] = DateTime.Now;
                    }
                } else if ((unknownCmdMatch = unknownCmdRegex.Match(commandEventArgs.Command.Argv(1))).Success)
                {
                    // Is this info about who is spectating who?
                    if (unknownCmdMatch.Groups.Count < 2) return;

                    string unknownCmd = unknownCmdMatch.Groups[1].Value.Trim().ToLower();


                    if (!infoPool.unsupportedCommands.Contains(unknownCmd)) // This isn't PERFECTLY threadsafe, but it should be fine. Shouldn't end up with too many duplicates.
                    {
                        serverWindow.addToLog($"NOTE: Command {unknownCmd} is not supported by this server. Noting.");
                        infoPool.unsupportedCommands.Add(unknownCmd);
                    }
                }
            }
        }
        
        
        
        void EvaluateTInfo(CommandEventArgs commandEventArgs)
        {
            int i;
            int theClient;

            int numSortedTeamPlayers = commandEventArgs.Command.Argv(1).Atoi();


            int PWRedFlag = jkaMode ? (int)JKAStuff.ItemList.powerup_t.PW_REDFLAG : (int)JOStuff.ItemList.powerup_t.PW_REDFLAG;
            int PWBlueFlag = jkaMode ? (int)JKAStuff.ItemList.powerup_t.PW_BLUEFLAG : (int)JOStuff.ItemList.powerup_t.PW_BLUEFLAG;

            for (i = 0; i < numSortedTeamPlayers; i++)
            {
                theClient = commandEventArgs.Command.Argv(i * 6 + 2).Atoi();

                //sortedTeamPlayers[i] = client;

                if(theClient < 0 || theClient >= 32)
                {
                    serverWindow.addToLog("TeamInfo client weird number "+theClient.ToString());
                } else { 

                    infoPool.playerInfo[theClient].location = commandEventArgs.Command.Argv(i * 6 + 3).Atoi();
                    infoPool.playerInfo[theClient].health = commandEventArgs.Command.Argv(i * 6 + 4).Atoi();
                    infoPool.playerInfo[theClient].armor = commandEventArgs.Command.Argv(i * 6 + 5).Atoi();
                    infoPool.playerInfo[theClient].curWeapon = commandEventArgs.Command.Argv(i * 6 + 6).Atoi();
                    infoPool.playerInfo[theClient].powerUps = commandEventArgs.Command.Argv(i * 6 + 7).Atoi(); // 2/3 places where powerups is transmitted
                    if (((infoPool.playerInfo[i].powerUps & (1 << PWRedFlag)) != 0) && infoPool.playerInfo[i].team != Team.Spectator) // Sometimes stuff seems to glitch and show spectators as having the flag
                    {
                        infoPool.teamInfo[(int)Team.Red].lastFlagCarrier = i;
                        infoPool.teamInfo[(int)Team.Red].lastFlagCarrierValid = true;
                        infoPool.teamInfo[(int)Team.Red].lastFlagCarrierValidUpdate = DateTime.Now;
                        infoPool.teamInfo[(int)Team.Red].lastFlagCarrierUpdate = DateTime.Now;
                    }
                    else if (((infoPool.playerInfo[i].powerUps & (1 << PWBlueFlag)) != 0) && infoPool.playerInfo[i].team != Team.Spectator) // Sometimes stuff seems to glitch and show spectators as having the flag
                    {
                        infoPool.teamInfo[(int)Team.Blue].lastFlagCarrier = i;
                        infoPool.teamInfo[(int)Team.Blue].lastFlagCarrierValid = true;
                        infoPool.teamInfo[(int)Team.Blue].lastFlagCarrierValidUpdate = DateTime.Now;
                        infoPool.teamInfo[(int)Team.Blue].lastFlagCarrierUpdate = DateTime.Now;
                    }
                }
            }
        }

        void EvaluateScore(CommandEventArgs commandEventArgs)
        {
            int i, powerups, readScores;

            readScores = commandEventArgs.Command.Argv(1).Atoi();

            int PWRedFlag = jkaMode ? (int)JKAStuff.ItemList.powerup_t.PW_REDFLAG : (int)JOStuff.ItemList.powerup_t.PW_REDFLAG;
            int PWBlueFlag = jkaMode ? (int)JKAStuff.ItemList.powerup_t.PW_BLUEFLAG : (int)JOStuff.ItemList.powerup_t.PW_BLUEFLAG;

            infoPool.teamInfo[(int)Team.Red].teamScore = commandEventArgs.Command.Argv(2).Atoi();
            infoPool.ScoreRed = commandEventArgs.Command.Argv(2).Atoi();
            infoPool.teamInfo[(int)Team.Blue].teamScore = commandEventArgs.Command.Argv(3).Atoi();
            infoPool.ScoreBlue = commandEventArgs.Command.Argv(3).Atoi();

            bool anyNonBotPlayerActive = false;
            bool anyPlayersActive = false;


            // Flexible detection of expanded scoreboard data. See how many entries per person we get. 
            // 14 is default. Some JKA mods (japlus and japro) send 15, an additional "killed" count.
            // Likewise, japlus and some mods can send more than the usual MaxClientScoreSend (20) scoreboard
            // players. Normally we would detect the server mod and see if we allow more than 20 players here to avoid weird crashes.
            // But instead we're doing the math to see if the numbers add up. If the offset ends up not working out,
            // we revert to the conservative legacy behavior. Could potentially result in weirdness sometimes but we'll see.
            // We'll throw  a little warning.
            int scoreboardOffset = (commandEventArgs.Command.Argc-4)/readScores;
            if(!(scoreboardOffset == 14 && !this.MBIIDetected) && !(scoreboardOffset == 15 && !this.MBIIDetected) && !(scoreboardOffset == 9 && this.MBIIDetected ))
            {
                if(this.MBIIDetected)
                {
                    serverWindow.addToLog($"Scoreboard error: calculated offset with MB II detected is not 9 (argc:{commandEventArgs.Command.Argc},readScores={readScores},offset:{scoreboardOffset}), wtf? Resetting score count and setting offset 9.", true, 1000 * 60 * 60); // Only show this error once per hour.
                    scoreboardOffset = 9;
                    if (readScores > JKClient.Common.MaxClientScoreSend)
                    {
                        readScores = JKClient.Common.MaxClientScoreSend;
                    }
                }
                else if(this.JAPlusDetected || this.JAProDetected)
                {
                    serverWindow.addToLog($"Scoreboard error: calculated offset is neither 14 nor 15 (argc:{commandEventArgs.Command.Argc},readScores={readScores},offset:{scoreboardOffset}), wtf? But detected japlus or japro. Resetting score count but using offset 15.", true, 1000 * 60 * 60); // Only show this error once per hour.
                    scoreboardOffset = 15;
                    if (readScores > JKClient.Common.MaxClientScoreSend)
                    {
                        readScores = JKClient.Common.MaxClientScoreSend;
                    }
                }
                else
                {
                    serverWindow.addToLog($"Scoreboard error: calculated offset is neither 14 nor 15 (argc:{commandEventArgs.Command.Argc},readScores={readScores},offset:{scoreboardOffset}), wtf? Defaulting to legacy behavior.", true,1000*60*60); // Only show this error once per hour.
                    scoreboardOffset = 14;
                    if (readScores > JKClient.Common.MaxClientScoreSend)
                    {
                        readScores = JKClient.Common.MaxClientScoreSend;
                    }
                }
                if (commandEventArgs.Command.Argc - 4 < readScores * scoreboardOffset)
                {
                    serverWindow.addToLog($"Scoreboard error: Not enough data received even after checks (argc-4:{commandEventArgs.Command.Argc - 4},readScores*offset={readScores * scoreboardOffset}), WTF? Reducing readScores until ok.", true, 1000 * 60 * 60); // Only show this error once per hour.
                    while(commandEventArgs.Command.Argc - 4 < readScores * scoreboardOffset)
                    {
                        readScores--;
                    }
                }
            }

            for (i = 0; i < readScores; i++)
            {
                //
                int clientNum = commandEventArgs.Command.Argv(i * scoreboardOffset + 4).Atoi();
                if (clientNum < 0 || clientNum >= client.ClientHandler.MaxClients)
                {
                    continue;
                }
                infoPool.playerInfo[clientNum].score.deathsIsFilled = false;
                if (!this.MBIIDetected) // Wtf is this i hear you say? MBII only has 9. And no, I have no idea which values are what for MBII anyway.
                {
                    infoPool.playerInfo[clientNum].score.shortScoresMBII = false;

                    infoPool.playerInfo[clientNum].score.client = commandEventArgs.Command.Argv(i * scoreboardOffset + 4).Atoi();
                    infoPool.playerInfo[clientNum].score.score = commandEventArgs.Command.Argv(i * scoreboardOffset + 5).Atoi();
                    infoPool.playerInfo[clientNum].score.ping = commandEventArgs.Command.Argv(i * scoreboardOffset + 6).Atoi();
                    infoPool.playerInfo[clientNum].score.time = commandEventArgs.Command.Argv(i * scoreboardOffset + 7).Atoi();
                    infoPool.playerInfo[clientNum].score.scoreFlags = commandEventArgs.Command.Argv(i * scoreboardOffset + 8).Atoi();
                    powerups = commandEventArgs.Command.Argv(i * scoreboardOffset + 9).Atoi();
                    infoPool.playerInfo[clientNum].score.powerUps = powerups; // duplicated from entities?
                    infoPool.playerInfo[clientNum].powerUps = powerups; // 3/3 places where powerups is transmitted
                    infoPool.playerInfo[clientNum].score.accuracy = commandEventArgs.Command.Argv(i * scoreboardOffset + 10).Atoi();
                    infoPool.playerInfo[clientNum].score.impressiveCount = commandEventArgs.Command.Argv(i * scoreboardOffset + 11).Atoi();
                    infoPool.playerInfo[clientNum].score.excellentCount = commandEventArgs.Command.Argv(i * scoreboardOffset + 12).Atoi();

                    infoPool.playerInfo[clientNum].score.guantletCount = commandEventArgs.Command.Argv(i * scoreboardOffset + 13).Atoi();
                    infoPool.playerInfo[clientNum].score.defendCount = commandEventArgs.Command.Argv(i * scoreboardOffset + 14).Atoi();
                    infoPool.playerInfo[clientNum].score.assistCount = commandEventArgs.Command.Argv(i * scoreboardOffset + 15).Atoi();
                    infoPool.playerInfo[clientNum].score.perfect = commandEventArgs.Command.Argv(i * scoreboardOffset + 16).Atoi() == 0 ? false : true;
                    infoPool.playerInfo[clientNum].score.captures = commandEventArgs.Command.Argv(i * scoreboardOffset + 17).Atoi();

                    if (scoreboardOffset == 15)
                    {
                        infoPool.playerInfo[clientNum].score.deaths = commandEventArgs.Command.Argv(i * scoreboardOffset + 18).Atoi();
                        infoPool.playerInfo[clientNum].score.deathsIsFilled = true;
                    }
                } else
                {
                    infoPool.playerInfo[clientNum].score.shortScoresMBII = true;
                    // Scores in MBII appear to be: ClientNum, Ping, Remaining Lives, Score, R, K, D, A, 1(not sure what)
                    infoPool.playerInfo[clientNum].score.client = commandEventArgs.Command.Argv(i * scoreboardOffset + 4).Atoi();
                    infoPool.playerInfo[clientNum].score.ping = commandEventArgs.Command.Argv(i * scoreboardOffset + 5).Atoi();
                    infoPool.playerInfo[clientNum].score.remainingLives = commandEventArgs.Command.Argv(i * scoreboardOffset + 6).Atoi();
                    infoPool.playerInfo[clientNum].score.score = commandEventArgs.Command.Argv(i * scoreboardOffset + 7).Atoi();
                    infoPool.playerInfo[clientNum].score.mbIIrounds = commandEventArgs.Command.Argv(i * scoreboardOffset + 8).Atoi();
                    infoPool.playerInfo[clientNum].score.kills = commandEventArgs.Command.Argv(i * scoreboardOffset + 9).Atoi();
                    infoPool.playerInfo[clientNum].score.deaths = commandEventArgs.Command.Argv(i * scoreboardOffset + 10).Atoi();
                    infoPool.playerInfo[clientNum].score.assistCount = commandEventArgs.Command.Argv(i * scoreboardOffset + 11).Atoi();
                    infoPool.playerInfo[clientNum].score.mbIImysteryValue = commandEventArgs.Command.Argv(i * scoreboardOffset + 12).Atoi();
                }
                infoPool.playerInfo[clientNum].lastScoreUpdated = DateTime.Now;

                if(infoPool.playerInfo[clientNum].score.ping != 0)
                {
                    infoPool.playerInfo[clientNum].score.lastNonZeroPing = DateTime.Now;
                    infoPool.playerInfo[clientNum].score.pingUpdatesSinceLastNonZeroPing = 0;
                } else
                {
                    infoPool.playerInfo[clientNum].score.pingUpdatesSinceLastNonZeroPing++;
                }
                if (infoPool.playerInfo[clientNum].team != Team.Spectator)
                {
                    anyPlayersActive = true;
                    if (!infoPool.playerInfo[clientNum].confirmedBot && (infoPool.playerInfo[clientNum].score.ping != 0 || infoPool.playerInfo[clientNum].score.pingUpdatesSinceLastNonZeroPing < 4)) // Be more safe. Anyone could have ping 0 by freak accident in theory.
                    {
                        anyNonBotPlayerActive = true;
                    }
                }

                if (((infoPool.playerInfo[clientNum].powerUps & (1 << PWRedFlag)) != 0) && infoPool.playerInfo[clientNum].team != Team.Spectator) // Sometimes stuff seems to glitch and show spectators as having the flag
                {
                    infoPool.teamInfo[(int)Team.Red].lastFlagCarrier = clientNum;
                    infoPool.teamInfo[(int)Team.Red].lastFlagCarrierValid = true;
                    infoPool.teamInfo[(int)Team.Red].lastFlagCarrierValidUpdate = DateTime.Now;
                    infoPool.teamInfo[(int)Team.Red].lastFlagCarrierUpdate = DateTime.Now;
                }
                else if (((infoPool.playerInfo[clientNum].powerUps & (1 << PWBlueFlag)) != 0) && infoPool.playerInfo[clientNum].team != Team.Spectator) // Sometimes stuff seems to glitch and show spectators as having the flag
                {
                    infoPool.teamInfo[(int)Team.Blue].lastFlagCarrier = clientNum;
                    infoPool.teamInfo[(int)Team.Blue].lastFlagCarrierValid = true;
                    infoPool.teamInfo[(int)Team.Blue].lastFlagCarrierValidUpdate = DateTime.Now;
                    infoPool.teamInfo[(int)Team.Blue].lastFlagCarrierUpdate = DateTime.Now;
                }

                // Serverside:
                //cl->ps.persistant[PERS_SCORE], // score
                //ping, // ping
                //(level.time - cl->pers.enterTime)/60000, // time
                //scoreFlags, // scoreflags
                //g_entities[level.sortedClients[i]].s.powerups, // powerups
                //accuracy,  // accuracy
                //cl->ps.persistant[PERS_IMPRESSIVE_COUNT], // impressive
                //cl->ps.persistant[PERS_EXCELLENT_COUNT], // excellent
                //cl->ps.persistant[PERS_GAUNTLET_FRAG_COUNT],  // gauntlet frag count
                //cl->ps.persistant[PERS_DEFEND_COUNT],  // defend count
                //cl->ps.persistant[PERS_ASSIST_COUNT],  // assist count 
                //perfect, // perfect count 
                //cl->ps.persistant[PERS_CAPTURES] // captures


            }
            if (!anyNonBotPlayerActive && anyPlayersActive)
            {
                infoPool.lastBotOnlyConfirmed = DateTime.Now;
            } else
            {
                infoPool.lastBotOnlyConfirmed = null;
            }
            snapsEnforcementUpdate();


            infoPool.lastScoreboardReceived = DateTime.Now;
        }

        int lastDemoIterator = 0;


        public async void startDemoRecord(int iterator=0)
        {
            shouldBeRecordingADemo = true;
            if(client.Status != ConnectionStatus.Active)
            {
                serverWindow.addToLog("Can't record demo when disconnected. But trying to queue recording in case we connect.");
                return;
            }
            if (isRecordingADemo)
            {
                if (!client.Demorecording)
                {

                    serverWindow.addToLog("isRecordingADemo indicates demo is already being recorded, but client says otherwise? Shouldn't really happen, some bug I guess. Try record anyway...");
                    //isRecordingADemo = false;
                } else
                {

                    serverWindow.addToLog("Demo is already being recorded...");
                    return;
                }
            }

            lastDemoIterator = iterator;

            serverWindow.addToLog("Initializing demo recording...");
            DateTime nowTime = DateTime.Now;
            string timeString = nowTime.ToString("yyyy-MM-dd_HH-mm-ss");
            string unusedDemoFilename = Helpers.GetUnusedDemoFilename(Helpers.MakeValidFileName(timeString + "-" + client.ServerInfo.MapName+"_"+client.ServerInfo.HostName+(iterator==0 ? "" : "_"+(iterator+1).ToString())), client.ServerInfo.Protocol);

            TaskCompletionSource<bool> firstPacketRecordedTCS = new TaskCompletionSource<bool>();

            _ = firstPacketRecordedTCS.Task.ContinueWith((Task<bool> s) =>
            {
                // Send a few commands that give interesting outputs, nothing more to it.

                // Some or most of these commands won't do anything on many servers.
                // On some servers they might display something
                // clientlist seems to have been a servercommand once, but its now a client command
                // In short, this stuff might not do anything except throw a wrong cmd error
                // But on some servers it might give mildly interesting output.

                // Need a timeout because of flood protection which is roughly speaking 1 command per second
                // We already do a scoreboard command every 2 seconds, so we have about 1 command every 2 seconds left
                // Go 3 seconds here to be safe. We also still need room to make commands for changing the camera angle.
                const int timeoutBetweenCommands = 3000;

                string time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss G\\MTzzz");
                ServerInfo curServerInfo = client.ServerInfo;
                string[] serverInfoParts = new string[] { "Recording demo",
                    time,
                    curServerInfo.Address?.ToString(),
                    curServerInfo.HostName,
                    curServerInfo.ServerGameVersionString,
                    curServerInfo.Location,
                    curServerInfo.Game,
                    curServerInfo.GameType.ToString(),
                    curServerInfo.GameName,
                    curServerInfo.MapName,
                    curServerInfo.Protocol.ToString(),
                    curServerInfo.Version.ToString(),
                    "sv_floodProtect: "+curServerInfo.FloodProtect.ToString(),
                    time,}; // Just for the occasional lost chat message, send time a second time. Most useful from these infos.
                serverInfoParts = Helpers.StringChunksOfMaxSize(serverInfoParts,140,", ^7^0^7", "^7^0^7"); // 150 is max message length. We split to 140 size chunks just to be safe
                foreach (string serverInfoPart in serverInfoParts)
                {
                    // Tell some info about the server... to myself
                    // Convenience feature.
                    leakyBucketRequester.requestExecution("tell " + client.clientNum + " \""+ serverInfoPart + "\"", RequestCategory.INFOCOMMANDS, 0, timeoutBetweenCommands, LeakyBucketRequester<string, RequestCategory>.RequestBehavior.ENQUEUE);
                }

                // NWH / CTFMod (?)
                leakyBucketRequester.requestExecution("info", RequestCategory.INFOCOMMANDS, 0, timeoutBetweenCommands, LeakyBucketRequester<string, RequestCategory>.RequestBehavior.ENQUEUE);
                leakyBucketRequester.requestExecution("afk", RequestCategory.INFOCOMMANDS, 0, timeoutBetweenCommands, LeakyBucketRequester<string, RequestCategory>.RequestBehavior.ENQUEUE);
                leakyBucketRequester.requestExecution("clientstatus", RequestCategory.INFOCOMMANDS, 0, timeoutBetweenCommands, LeakyBucketRequester<string, RequestCategory>.RequestBehavior.ENQUEUE);
                leakyBucketRequester.requestExecution("specs", RequestCategory.INFOCOMMANDS, 0, timeoutBetweenCommands, LeakyBucketRequester<string, RequestCategory>.RequestBehavior.ENQUEUE);
                leakyBucketRequester.requestExecution("clientstatus", RequestCategory.INFOCOMMANDS, 0, timeoutBetweenCommands, LeakyBucketRequester<string, RequestCategory>.RequestBehavior.ENQUEUE);

                if (client.ServerInfo.GameType == GameType.FFA && client.ServerInfo.NWH == false && !this.jkaMode && !_connectionOptions.silentMode)
                { // replace with more sophisticated detection
                    // doing a detection here to not annoy ctf players.
                    // will still annoy ffa players until better detection.
                    // Show top 10 scores at start of demo recording.
                    leakyBucketRequester.requestExecution("say_team !top", RequestCategory.INFOCOMMANDS, 0, timeoutBetweenCommands, LeakyBucketRequester<string, RequestCategory>.RequestBehavior.ENQUEUE);
                }

                // TwiMod (DARK etc)
                leakyBucketRequester.requestExecution("ammodinfo", RequestCategory.INFOCOMMANDS, 0, timeoutBetweenCommands, LeakyBucketRequester<string, RequestCategory>.RequestBehavior.ENQUEUE);
                leakyBucketRequester.requestExecution("ammodinfo_twitch", RequestCategory.INFOCOMMANDS, 0, timeoutBetweenCommands, LeakyBucketRequester<string, RequestCategory>.RequestBehavior.ENQUEUE);
                if (client.ServerInfo.GameType == GameType.FFA && client.ServerInfo.NWH == false && !this.jkaMode && !_connectionOptions.silentMode) // Might not be accurate idk
                {
                    leakyBucketRequester.requestExecution("say_team !dimensions", RequestCategory.INFOCOMMANDS, 0, timeoutBetweenCommands, LeakyBucketRequester<string, RequestCategory>.RequestBehavior.ENQUEUE);
                    leakyBucketRequester.requestExecution("say_team !where", RequestCategory.INFOCOMMANDS, 0, timeoutBetweenCommands, LeakyBucketRequester<string, RequestCategory>.RequestBehavior.ENQUEUE);
                }

                // Whatever
                leakyBucketRequester.requestExecution("serverstatus", RequestCategory.INFOCOMMANDS, 0, timeoutBetweenCommands, LeakyBucketRequester<string, RequestCategory>.RequestBehavior.ENQUEUE);
                leakyBucketRequester.requestExecution("clientinfo", RequestCategory.INFOCOMMANDS, 0, timeoutBetweenCommands, LeakyBucketRequester<string, RequestCategory>.RequestBehavior.ENQUEUE);
                leakyBucketRequester.requestExecution("clientlist", RequestCategory.INFOCOMMANDS, 0, timeoutBetweenCommands, LeakyBucketRequester<string, RequestCategory>.RequestBehavior.ENQUEUE);

                /*client.ExecuteCommand("info");
                System.Threading.Thread.Sleep(timeoutBetweenCommands);
                client.ExecuteCommand("afk");
                System.Threading.Thread.Sleep(timeoutBetweenCommands);
                client.ExecuteCommand("clientstatus");
                System.Threading.Thread.Sleep(timeoutBetweenCommands);
                client.ExecuteCommand("specs");
                System.Threading.Thread.Sleep(timeoutBetweenCommands);

                // OC Defrag
                if(client.ServerInfo.GameType == GameType.FFA) { // replace with more sophisticated detection
                    // doing a detection here to not annoy ctf players.
                    // will still annoy ffa players until better detection.
                    client.ExecuteCommand("say_team !top"); // Show top 10 scores at start of demo recording.
                    System.Threading.Thread.Sleep(timeoutBetweenCommands);
                }

                // TwiMod (DARK etc)
                client.ExecuteCommand("ammodinfo"); 
                System.Threading.Thread.Sleep(timeoutBetweenCommands);

                // Whatever
                client.ExecuteCommand("clientinfo");
                System.Threading.Thread.Sleep(timeoutBetweenCommands);
                client.ExecuteCommand("clientlist");*/
            });

            bool success = await client.Record_f(new DemoName_t {name= unusedDemoFilename,time=nowTime }, firstPacketRecordedTCS);

            if (success)
            {

                serverWindow.addToLog("Demo recording started.");
                isRecordingADemo = true;
                if (_connectionOptions.demoTimeColorNames)
                {
                    updateName();
                }
            }
            else
            {

                serverWindow.addToLog("Demo recording failed to start for some reason.");
                isRecordingADemo = false;
            }
        }
        public void stopDemoRecord(bool afterInvoluntaryDisconnect = false)
        {
            if (!afterInvoluntaryDisconnect)
            {
                shouldBeRecordingADemo = false;
            }
            serverWindow.addToLog("Stopping demo recording...");
            client.StopRecord_f();
            updateName();
            isRecordingADemo = false;
            serverWindow.addToLog("Demo recording stopped.");
        }

    }

    // means of death
    enum MeansOfDeath {
        MOD_UNKNOWN,
        MOD_STUN_BATON,
        MOD_MELEE,
        MOD_SABER,
        MOD_BRYAR_PISTOL,
        MOD_BRYAR_PISTOL_ALT,
        MOD_BLASTER,
        MOD_DISRUPTOR,
        MOD_DISRUPTOR_SPLASH,
        MOD_DISRUPTOR_SNIPER,
        MOD_BOWCASTER,
        MOD_REPEATER,
        MOD_REPEATER_ALT,
        MOD_REPEATER_ALT_SPLASH,
        MOD_DEMP2,
        MOD_DEMP2_ALT,
        MOD_FLECHETTE,
        MOD_FLECHETTE_ALT_SPLASH,
        MOD_ROCKET,
        MOD_ROCKET_SPLASH,
        MOD_ROCKET_HOMING,
        MOD_ROCKET_HOMING_SPLASH,
        MOD_THERMAL,
        MOD_THERMAL_SPLASH,
        MOD_TRIP_MINE_SPLASH,
        MOD_TIMED_MINE_SPLASH,
        MOD_DET_PACK_SPLASH,
        MOD_FORCE_DARK,
        MOD_SENTRY,
        MOD_WATER,
        MOD_SLIME,
        MOD_LAVA,
        MOD_CRUSH,
        MOD_TELEFRAG,
        MOD_FALLING,
        MOD_SUICIDE,
        MOD_TARGET_LASER,
        MOD_TRIGGER_HURT,
        MOD_MAX
    }
}
