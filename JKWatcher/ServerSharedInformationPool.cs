﻿using JKClient;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;

namespace JKWatcher
{
    // TODO MAke it easier to reset these between games or when maps change. Probably just make new new STatements?
    public struct PlayerInfo
    {
        #region position
        public Vector3 position;
        public DateTime? lastPositionUpdate; // Last time the player position alone was updated (from events or such)
        public Vector3 angles;
        public Vector3 velocity;
        public bool IsAlive;
        public DateTime? lastFullPositionUpdate; // Last time all the info above was updated from entities
        #endregion

        public Vector3 lastDeathPosition;
        public DateTime? lastDeath;

        #region score
        public PlayerScore score;
        public DateTime? lastScoreUpdated;
        #endregion

        #region nwh
        public int nwhSpectatedPlayer { get; set; }
        public DateTime? nwhSpectatedPlayerLastUpdate;
        #endregion

        #region clientinfo
        public string name { get; set; }
        public Team team { get; set; }
        public bool infoValid { get; set; }
        public int clientNum { get; set; }
        public DateTime? lastClientInfoUpdate;
        #endregion

        #region tinfo
        public volatile int location;       // location index for team mode
        public volatile int health;         // you only get this info about your teammates
        public volatile int armor;
        public volatile int curWeapon;
        public volatile int powerUps;		// so can display quad/flag status
        #endregion
    }
    public struct PlayerScore
    {
        public volatile int client;
        public volatile int score;
        public volatile int ping;
        public volatile int time;
        public volatile int scoreFlags;
        public volatile int powerUps;
        public volatile int accuracy;
        public volatile int impressiveCount; // rets?
        public volatile int excellentCount;
        public volatile int guantletCount;
        public volatile int defendCount; // bc?
        public volatile int assistCount;
        public volatile int captures; // captures
        public volatile int deaths; // times he got killed. Some JKA mods send this.
        public volatile bool deathsIsFilled; // Indicate if killed value was sent
        public volatile bool perfect;
        public volatile int team;
        public DateTime? lastNonZeroPing;
        public volatile int pingUpdatesSinceLastNonZeroPing;
    }


    public struct TeamInfo
    {

        public volatile int teamScore;

        public volatile FlagStatus flag;
        public DateTime? lastFlagUpdate;
        public DateTime? lastTimeFlagWasSeenAtBase;

        // The following infos are all related to the flag of the team this struct is for
        public volatile int flagItemNumber;

        public volatile int lastFlagCarrier;
        public volatile bool lastFlagCarrierValid; // We set this to false if the flag is dropped or goes back to base. Or we might assume the wrong carrier when the flag is taken again if the proper carrier hasn't been set yet.
        public DateTime? lastFlagCarrierValidUpdate;
        public DateTime? lastFlagCarrierFragged;
        public DateTime? lastFlagCarrierWorldDeath;
        public DateTime? lastFlagCarrierUpdate;

        // Positions of flag bases ( as fallback)
        public Vector3 flagBasePosition;
        public volatile int flagBaseEntityNumber;
        public DateTime? lastFlagBasePositionUpdate;

        // Positions of base flag items (the flag item is separate from the flag base)
        public Vector3 flagBaseItemPosition;
        public volatile int flagBaseItemEntityNumber;
        public DateTime? lastFlagBaseItemPositionUpdate;

        // Positions of dropped flags
        public Vector3 flagDroppedPosition;
        public volatile int droppedFlagEntityNumber;
        public DateTime? lastFlagDroppedPositionUpdate;

    }

    // Todo reset stuff on level restart and especially map change
    public class ServerSharedInformationPool : INotifyPropertyChanged
    {
        public bool isIntermission { get; set; } = false;

        private int gameTime = 0;
        public string GameTime { get; private set; }
        public string MapName { get; set; }
        public int ScoreRed { get; set; }
        public int ScoreBlue { get; set; }

        public bool NoActivePlayers { get; set; }

        public DateTime? lastBotOnlyConfirmed = null;

        public void setGameTime(int gameTime)
        {
            this.gameTime = gameTime;
            int msec = gameTime;
            int secs = msec / 1000;
            int mins = secs / 60;

            secs = secs % 60;
            msec = msec % 1000;

            GameTime = mins.ToString() +":"+ secs.ToString("D2");
        }

        public PlayerInfo[] playerInfo = new PlayerInfo[new JOClientHandler(ProtocolVersion.Protocol15,ClientVersion.JO_v1_02).MaxClients];
        public TeamInfo[] teamInfo = new TeamInfo[Enum.GetNames(typeof(JKClient.Team)).Length];

        public event PropertyChangedEventHandler PropertyChanged;


        private bool jkaMode = false;
        public ServerSharedInformationPool(bool jkaModeA)
        {
            jkaMode = jkaModeA;
            if (jkaMode)
            {
                teamInfo[(int)JKClient.Team.Red].flagItemNumber = JKAStuff.ItemList.BG_FindItemForPowerup(JKAStuff.ItemList.powerup_t.PW_REDFLAG).Value;
                teamInfo[(int)JKClient.Team.Blue].flagItemNumber = JKAStuff.ItemList.BG_FindItemForPowerup(JKAStuff.ItemList.powerup_t.PW_BLUEFLAG).Value;

            } else
            {
                teamInfo[(int)JKClient.Team.Red].flagItemNumber = JOStuff.ItemList.BG_FindItemForPowerup(JOStuff.ItemList.powerup_t.PW_REDFLAG).Value;
                teamInfo[(int)JKClient.Team.Blue].flagItemNumber = JOStuff.ItemList.BG_FindItemForPowerup(JOStuff.ItemList.powerup_t.PW_BLUEFLAG).Value;
            }
        }

        public void ResetInfo()
        {
            playerInfo = new PlayerInfo[new JOClientHandler(ProtocolVersion.Protocol15, ClientVersion.JO_v1_02).MaxClients];
            teamInfo = new TeamInfo[Enum.GetNames(typeof(JKClient.Team)).Length];
            if (jkaMode)
            {
                teamInfo[(int)JKClient.Team.Red].flagItemNumber = JKAStuff.ItemList.BG_FindItemForPowerup(JKAStuff.ItemList.powerup_t.PW_REDFLAG).Value;
                teamInfo[(int)JKClient.Team.Blue].flagItemNumber = JKAStuff.ItemList.BG_FindItemForPowerup(JKAStuff.ItemList.powerup_t.PW_BLUEFLAG).Value;
            } else
            {
                teamInfo[(int)JKClient.Team.Red].flagItemNumber = JOStuff.ItemList.BG_FindItemForPowerup(JOStuff.ItemList.powerup_t.PW_REDFLAG).Value;
                teamInfo[(int)JKClient.Team.Blue].flagItemNumber = JOStuff.ItemList.BG_FindItemForPowerup(JOStuff.ItemList.powerup_t.PW_BLUEFLAG).Value;
            }
        }
    }

    public enum FlagStatus : int
    {
        FLAG_ATBASE = 0,
        FLAG_TAKEN,         // CTF
        FLAG_TAKEN_RED,     // One Flag CTF
        FLAG_TAKEN_BLUE,    // One Flag CTF
        FLAG_DROPPED
    }
}
