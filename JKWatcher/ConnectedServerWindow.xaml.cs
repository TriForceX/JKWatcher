﻿using JKClient;
using Client = JKClient.JKClient;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Drawing;
using System.Threading;
using System.Collections.Concurrent;

namespace JKWatcher
{
    /// <summary>
    /// Interaction logic for ConnectedServerWindow.xaml
    /// </summary>
    public partial class ConnectedServerWindow : Window, ConnectionProvider
    {

        public bool LogColoredEnabled { get; set; } = true;
        public bool LogPlainEnabled { get; set; } = false;
        public bool DrawMiniMap { get; set; } = false;

        //private ServerInfo serverInfo = null;
        //private string ip;
        public NetAddress netAddress { get; private set; }
        public ProtocolVersion protocol { get; private set; }
        private string serverName = null;
        public string ServerName
        {
            get
            {
                return serverName;
            }
            set
            {
                if (value != serverName)
                {
                    Dispatcher.Invoke(()=> {
                        this.Title = netAddress.ToString() + " (" + value + ")";
                    });
                }
                serverName = value;
            }
        }

        private FullyObservableCollection<Connection> connections = new FullyObservableCollection<Connection>();
        private FullyObservableCollection<CameraOperator> cameraOperators = new FullyObservableCollection<CameraOperator>();

        private const int maxLogLength = 10000;
        private string logString = "Begin of Log\n";

        private ServerSharedInformationPool infoPool { get; set; }

        private List<CancellationTokenSource> backgroundTasks = new List<CancellationTokenSource>();

        //public bool verboseOutput { get; private set; } = false;
        public int verboseOutput { get; private set; } = 4;

        private string password = null;

        // TODO: Send "score" command to server every second or 2 so we always have up to date scoreboards. will eat a bit more space maybe but should be cool. make it possible to disable this via some option, or to set interval

        public ConnectedServerWindow(NetAddress netAddressA, ProtocolVersion protocolA, string serverNameA = null, string passwordA = null)
        {
            //this.DataContext = this;

            //serverInfo = serverInfoA;
            netAddress = netAddressA;
            protocol = protocolA;
            password = passwordA;
            InitializeComponent();
            this.Title = netAddressA.ToString();
            if(serverNameA != null)
            {
                ServerName = serverNameA; // This will also change title.
            }

            connectionsDataGrid.ItemsSource = connections;
            cameraOperatorsDataGrid.ItemsSource = cameraOperators;

            infoPool = new ServerSharedInformationPool();

            gameTimeTxt.DataContext = infoPool;
            mapNameTxt.DataContext = infoPool;
            scoreRedTxt.DataContext = infoPool;
            scoreBlueTxt.DataContext = infoPool;

            lock (connections)
            {
                //connections.Add(new Connection(serverInfo, this,  infoPool));
                connections.Add(new Connection(netAddress,protocol, this,  infoPool,password));
            }
            updateIndices();

            logTxt.Text = "";
            addToLog("Begin of Log\n");

            this.Closed += ConnectedServerWindow_Closed;

            var tokenSource = new CancellationTokenSource();
            CancellationToken ct = tokenSource.Token;
            Task.Factory.StartNew(() => { miniMapUpdater(ct); }, ct, TaskCreationOptions.LongRunning,TaskScheduler.Default).ContinueWith((t) => {
                addToLog(t.Exception.ToString(), true);
            }, TaskContinuationOptions.OnlyOnFaulted);
            backgroundTasks.Add(tokenSource);

            tokenSource = new CancellationTokenSource();
            ct = tokenSource.Token;
            Task.Factory.StartNew(() => { scoreBoardRequester(ct); }, ct, TaskCreationOptions.LongRunning,TaskScheduler.Default).ContinueWith((t) => {
                addToLog(t.Exception.ToString(),true);
            }, TaskContinuationOptions.OnlyOnFaulted);
            backgroundTasks.Add(tokenSource);

            startLogStringUpdater();

        } 
        
        /*public ConnectedServerWindow(string ipA, ProtocolVersion protocolA)
        {
            //this.DataContext = this;
            //serverInfo = serverInfoA;
            ip = ipA;
            protocol = protocolA;
            InitializeComponent();
            this.Title = ipA + " ( Manual connect )"; // TODO Update name later

            connectionsDataGrid.ItemsSource = connections;
            cameraOperatorsDataGrid.ItemsSource = cameraOperators;

            infoPool = new ServerSharedInformationPool();

            gameTimeTxt.DataContext = infoPool;

            lock (connections)
            {
                connections.Add(new Connection(ipA, protocolA, this,  infoPool));
            }
            updateIndices();

            logTxt.Text = "";
            addToLog("Begin of Log\n");

            this.Closed += ConnectedServerWindow_Closed;

            var tokenSource = new CancellationTokenSource();
            CancellationToken ct = tokenSource.Token;
            Task.Factory.StartNew(() => { miniMapUpdater(ct); }, ct, TaskCreationOptions.LongRunning,TaskScheduler.Default).ContinueWith((t) => {
                addToLog(t.Exception.ToString(), true);
            }, TaskContinuationOptions.OnlyOnFaulted);
            backgroundTasks.Add(tokenSource);

            tokenSource = new CancellationTokenSource();
            ct = tokenSource.Token;
            Task.Factory.StartNew(() => { scoreBoardRequester(ct); }, ct, TaskCreationOptions.LongRunning,TaskScheduler.Default).ContinueWith((t) => {
                addToLog(t.Exception.ToString(),true);
            }, TaskContinuationOptions.OnlyOnFaulted);
            backgroundTasks.Add(tokenSource);

            startLogStringUpdater();

        }*/

        private void startLogStringUpdater()
        {
            var tokenSource = new CancellationTokenSource();
            CancellationToken ct = tokenSource.Token;
            Task.Factory.StartNew(() => { logStringUpdater(ct); }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default).ContinueWith((t) => {
                //addToLog(t.Exception.ToString(),true);
                Helpers.logToFile(new string[] { t.Exception.ToString() });
                Helpers.logToFile(dequeuedStrings.ToArray());
                Helpers.logToFile(stringsToForceWriteToLogFile.ToArray());
                dequeuedStrings.Clear();
                stringsToForceWriteToLogFile.Clear();
                startLogStringUpdater();
            }, TaskContinuationOptions.OnlyOnFaulted);
            backgroundTasks.Add(tokenSource);
        }

        private void updateIndices()
        {
            lock (connections)
            {
                for (int i = 0; i < connections.Count; i++)
                {
                    connections[i].Index = i;
                }
            }
            lock (cameraOperators)
            {
                for (int i = 0; i < cameraOperators.Count; i++)
                {
                    cameraOperators[i].Index = i;
                }
            }
        }

        public struct LogQueueItem {
            public string logString;
            public bool forceLogToFile;
            public DateTime time;
        }

        ConcurrentQueue<LogQueueItem> logQueue = new ConcurrentQueue<LogQueueItem>();
        List<int> linesRunCounts = new List<int>();
        const int countOfLineSAllowed = 100;

        List<string> dequeuedStrings = new List<string>();
        List<string> stringsToForceWriteToLogFile = new List<string>();

        string timeString = "", lastTimeString = "";
        private void logStringUpdater(CancellationToken ct)
        {
            while (true)
            {
                System.Threading.Thread.Sleep(10);
                ct.ThrowIfCancellationRequested();

                LogQueueItem stringToAdd;
                while (logQueue.TryDequeue(out stringToAdd))
                {
                    timeString = $"[{stringToAdd.time.ToString("yyyy-MM-dd HH:mm:ss")}]";
                    bool timeStringChanged = timeString != lastTimeString;
                    if (stringToAdd.forceLogToFile)
                    {
                        if(timeStringChanged) stringsToForceWriteToLogFile.Add(timeString);
                        stringsToForceWriteToLogFile.Add(stringToAdd.logString);
                    }
                    if (timeStringChanged) dequeuedStrings.Add(timeString);
                    dequeuedStrings.Add(stringToAdd.logString);
                    lastTimeString = timeString;
                }

                if (LogColoredEnabled)
                {
                    addStringsToColored(dequeuedStrings.ToArray());
                }
                if (LogPlainEnabled)
                {
                    addStringsToPlain(dequeuedStrings.ToArray());
                }

                if(stringsToForceWriteToLogFile.Count > 0)
                {
                    Helpers.logToFile(stringsToForceWriteToLogFile.ToArray());
                }
//#if DEBUG
                // TODO Clean this up, make it get serverInfo from connections if connected via ip.
                //Helpers.debugLogToFile(serverInfo == null ? netAddress.ToString() : serverInfo.Address.ToString() + "_" + serverInfo.HostName , dequeuedStrings.ToArray());
                Helpers.debugLogToFile(serverName == null ? netAddress.ToString() : netAddress.ToString() + "_" + serverName, dequeuedStrings.ToArray());
//#endif

                dequeuedStrings.Clear();
                stringsToForceWriteToLogFile.Clear();
            }
            
        }

        private void addStringsToColored(string[] stringsToAdd)
        {
            Dispatcher.Invoke(() => {

                List<Inline> linesToAdd = new List<Inline>();

                foreach (string stringToAdd in stringsToAdd)
                {
                    Run[] runs = Q3ColorFormatter.Q3StringToInlineArray(stringToAdd);
                    if (runs.Length == 0) continue;
                    linesToAdd.AddRange(runs);
                    linesToAdd.Add(new LineBreak());
                    linesRunCounts.Add(runs.Length + 1);
                }

                // If there are too many lines, count how many runs we need to remove
                int countOfRunsToRemove = 0;
                while (linesRunCounts.Count > countOfLineSAllowed)
                {
                    countOfRunsToRemove += linesRunCounts[0];
                    linesRunCounts.RemoveAt(0);
                }

                for (int i = 0; i < countOfRunsToRemove; i++)
                {
                    logTxt.Inlines.Remove(logTxt.Inlines.FirstInline);
                }
                logTxt.Inlines.AddRange(linesToAdd);
            });
        }
        private void addStringsToPlain(string[] stringsToAdd)
        {
            lock (logString)
            {
                foreach (string stringToAddIt in stringsToAdd)
                {
                    string stringToAdd = stringToAddIt + "\n";
                    int newLogLength = logString.Length + stringToAdd.Length;
                    if (newLogLength <= maxLogLength)
                    {
                        logString += stringToAdd;
                    }
                    else
                    {
                        int cutAway = newLogLength - maxLogLength;
                        logString = logString.Substring(cutAway) + stringToAdd;
                    }
                }
            }

            Dispatcher.Invoke(() => {
                lock (logString)
                {
                    logTxtPlain.Text = logString;
                }
            });
        }
        
        // Old style log string updater without colors:
        /* private void logStringUpdater(CancellationToken ct)
        {
            while (true)
            {
                System.Threading.Thread.Sleep(10);
                ct.ThrowIfCancellationRequested();

                string stringToAdd;
                
                while(logQueue.TryDequeue(out stringToAdd))
                {
                    lock (logString)
                    {
                        stringToAdd += "\n";
                        int newLogLength = logString.Length + stringToAdd.Length;
                        if (newLogLength <= maxLogLength)
                        {
                            logString += stringToAdd;
                        }
                        else
                        {
                            int cutAway = newLogLength - maxLogLength;
                            logString = logString.Substring(cutAway) + stringToAdd;
                        }
                    }
                }
                
                Dispatcher.Invoke(() => {
                    lock (logString)
                    {
                        logTxt.Text = logString;
                    }
                });
            }
            
        }*/



        public void addToLog(string someString,bool forceLogToFile=false)
        {
            logQueue.Enqueue(new LogQueueItem() { logString= someString,forceLogToFile=forceLogToFile,time=DateTime.Now });
        }


        private unsafe void scoreBoardRequester(CancellationToken ct)
        {
            while (true)
            {
                //System.Threading.Thread.Sleep(1000); // wanted to do 1 every second but alas, it triggers rate limit that is 1 per second apparently, if i want to execute any other commands.
                System.Threading.Thread.Sleep(2000);
                ct.ThrowIfCancellationRequested();

                foreach(Connection connection in connections)
                {
                    if(connection.client.Status == ConnectionStatus.Active)
                    {
                        //connection.client.ExecuteCommand("score");
                        connection.leakyBucketRequester.requestExecution("score",RequestCategory.SCOREBOARD,0,2000,LeakyBucketRequester<string, RequestCategory>.RequestBehavior.DELETE_PREVIOUS_OF_SAME_TYPE);
                    }
                }
            }
        }

        const float miniMapOutdatedDrawTime = 1; // in seconds.

        private unsafe void miniMapUpdater(CancellationToken ct)
        {
            float minX = float.PositiveInfinity, maxX = float.NegativeInfinity, minY = float.PositiveInfinity, maxY = float.NegativeInfinity;
            while (true) {

                System.Threading.Thread.Sleep(100);
                ct.ThrowIfCancellationRequested();

                if (infoPool.playerInfo == null || !DrawMiniMap) continue;

                int imageWidth = (int)miniMapContainer.ActualWidth;
                int imageHeight = (int)miniMapContainer.ActualHeight;

                if (imageWidth < 5 || imageHeight < 5) continue; // avoid crashes and shit

                // We flip imageHeight and imageWidth because it's more efficient to work on rows than on columns. We later rotate the image into the proper position
                ByteImage miniMapImage = Helpers.BitmapToByteArray(new Bitmap(imageWidth, imageHeight, System.Drawing.Imaging.PixelFormat.Format24bppRgb));
                int stride = miniMapImage.stride;

                // Pass 1: Get bounds of all player entities
                /*ClientEntity[] entities = connections[0].client.Entities;
                if(entities == null)
                {
                    continue;
                }*/
                
                for(int i = 0; i < infoPool.playerInfo.Length; i++)
                {
                    minX = Math.Min(minX, -infoPool.playerInfo[i].position.X);
                    maxX = Math.Max(maxX, -infoPool.playerInfo[i].position.X);
                    minY = Math.Min(minY, infoPool.playerInfo[i].position.Y);
                    maxY = Math.Max(maxY, infoPool.playerInfo[i].position.Y);
                }

                // Pass 2: Draw players as pixels
                float xRange = maxX - minX, yRange = maxY-minY;
                float x, y;
                int imageX, imageY;
                int byteOffset;
                for (int i = 0; i < infoPool.playerInfo.Length; i++)
                {
                    if(infoPool.playerInfo[i].lastFullPositionUpdate == null)
                    {
                        continue; // don't have any position data
                    }
                    if ((DateTime.Now - infoPool.playerInfo[i].lastFullPositionUpdate.Value).TotalSeconds > miniMapOutdatedDrawTime)
                    {
                        continue; // data too old (probably bc out of sight)
                    }
                    if(infoPool.playerInfo[i].lastClientInfoUpdate == null)
                    {
                        continue; // don't have any client info.
                    }
                    x = -infoPool.playerInfo[i].position.X;
                    y = infoPool.playerInfo[i].position.Y;
                    imageX = Math.Clamp((int)( (x - minX) / xRange *(float)(imageWidth-1f)),0,imageWidth-1);
                    imageY = Math.Clamp((int)((y - minY) / yRange *(float)(imageHeight-1f)),0,imageHeight-1);
                    byteOffset = imageY * stride + imageX * 3;
                    if(infoPool.playerInfo[i].team == Team.Red)
                    {
                        byteOffset += 2; // red pixel. blue is just 0.
                    } else if (infoPool.playerInfo[i].team != Team.Blue)
                    {
                        byteOffset += 1; // Just make it green then, not sure what it is.
                    }

                    miniMapImage.imageData[byteOffset] = 255;
                }

                
                //statsImageBitmap.RotateFlip(RotateFlipType.Rotate270FlipNone);
                Dispatcher.Invoke(()=> {
                    Bitmap miniMapImageBitmap = Helpers.ByteArrayToBitmap(miniMapImage);
                    miniMap.Source = Helpers.BitmapToImageSource(miniMapImageBitmap);
                    miniMapImageBitmap.Dispose();
                });
            }
        }

        public void createCTFOperator()
        {
            createCameraOperator<CameraOperators.CTFCameraOperatorRedBlue>();
        }

        private void createCameraOperator<T>() where T:CameraOperator, new()
        {
            
            lock (cameraOperators)
            {
                T camOperator = new T();
                camOperator.Errored += CamOperator_Errored;
                int requiredConnectionCount = camOperator.getRequiredConnectionCount();
                Connection[] connectionsForCamOperator = getUnboundConnections(requiredConnectionCount);
                camOperator.provideConnections(connectionsForCamOperator);
                camOperator.provideServerSharedInformationPool(infoPool);
                camOperator.provideConnectionProvider(this);
                camOperator.Initialize();
                cameraOperators.Add(camOperator);
                updateIndices();
            }
        }

        private void CamOperator_Errored(object sender, CameraOperator.ErroredEventArgs e)
        {
            addToLog("Camera Operator error: " + e.Exception.ToString(),true);
        }

        public Connection[] getUnboundConnections(int count)
        {
            List<Connection> retVal = new List<Connection>();

            foreach(Connection connection in connections)
            {
                if(connection.CameraOperator == null)
                {
                    retVal.Add(connection);
                    if(retVal.Count == count)
                    {
                        break;
                    }
                }
            }

            while(retVal.Count < count)
            {
                //Connection newConnection = new Connection(connections[0].client.ServerInfo, this,infoPool);
                Connection newConnection = new Connection(netAddress,protocol, this,infoPool,password);
                lock (connections)
                {
                    connections.Add(newConnection);
                }
                updateIndices();
                retVal.Add(newConnection);
            }

            return retVal.ToArray();
        }

        private void ConnectedServerWindow_Closed(object sender, EventArgs e)
        {
            CloseDown();
        }

        ~ConnectedServerWindow() // Not sure if needed tbh
        {
            CloseDown();
        }

        private void CloseDown()
        {
            foreach(CancellationTokenSource backgroundTask in backgroundTasks)
            {
                backgroundTask.Cancel();
            }

            //if (connections.Count == 0) return; // doesnt really matter.

            foreach (CameraOperator op in cameraOperators)
            {
                lock (connections)
                {
                    op.Destroy();
                }
                //cameraOperators.Remove(op); // Don't , we're inside for each
                updateIndices();
                op.Errored -= CamOperator_Errored;
            }
            cameraOperators.Clear();
            lock (connections)
            {
                foreach (Connection connection in connections)
                {
                    connection.stopDemoRecord();
                    //connection.disconnect();
                    connection.CloseDown();
                    //connections.Remove(connection); // Don't, we're inside foreach
                }
                connections.Clear();
            }

        }

        public void recordAll()
        {
            int i = 0;
            foreach (Connection conn in connections)
            {
                if (conn != null)
                {
                    conn.startDemoRecord(i++);
                }
            }
        }

        
        private void recordBtn_Click(object sender, RoutedEventArgs e)
        {
            if (connections.Count == 0) return;

            // we make a copy of the selected items because otherwise the command might change something
            // that also results in a change of selecteditems and then it would only get the first item.
            List<Connection> conns = connectionsDataGrid.SelectedItems.Cast<Connection>().ToList();

            int i = 0;
            foreach (Connection conn in conns)
            {
                if (conn != null && !conn.isRecordingADemo)
                {
                    conn.startDemoRecord(i++);
                }
            }

            /*foreach (Connection connection in connections)
            {
                if(connection.client.Status == ConnectionStatus.Active)
                {

                    connection.startDemoRecord();
                    break;
                }
            }*/
        }

        private void stopRecordBtn_Click(object sender, RoutedEventArgs e)
        {
            if (connections.Count == 0) return;

            // we make a copy of the selected items because otherwise the command might change something
            // that also results in a change of selecteditems and then it would only get the first item.
            List<Connection> conns = connectionsDataGrid.SelectedItems.Cast<Connection>().ToList();

            foreach (Connection conn in conns)
            {
                if (conn != null && conn.isRecordingADemo)
                {
                    conn.stopDemoRecord();
                }
            }

            /*foreach (Connection connection in connections)
            {
                if (connection.client.Demorecording)
                {

                    connection.stopDemoRecord();
                    break;
                }
            }*/
        }

        private void commandSendBtn_Click(object sender, RoutedEventArgs e)
        {
            List<Connection> conns = connectionsDataGrid.SelectedItems.Cast<Connection>().ToList();

            DoExecuteCommand(commandLine.Text, conns.ToArray());
            /*foreach (Connection connection in conns)
            {
                if (connection.client.Status == ConnectionStatus.Active)
                {
                    string command = commandLine.Text;
                    //commandLine.Text = "";
                    //connection.client.ExecuteCommand(command);
                    connection.leakyBucketRequester.requestExecution(command, RequestCategory.NONE,1,0,LeakyBucketRequester<string, RequestCategory>.RequestBehavior.ENQUEUE);

                    addToLog("Command \"" + command + "\" sent.");
                }
            }*/
        }

        private void updateButtonEnablednesses()
        {
            bool connectionsSelected = connectionsDataGrid.Items.Count > 0 && connectionsDataGrid.SelectedItems.Count > 0;
            bool cameraOperatorsSelected = cameraOperatorsDataGrid.Items.Count > 0 && cameraOperatorsDataGrid.SelectedItems.Count > 0;
            bool playersSelected = playerListDataGrid.Items.Count > 0 && playerListDataGrid.SelectedItems.Count > 0;

            delBtn.IsEnabled = connectionsSelected;
            reconBtn.IsEnabled = connectionsSelected;
            recordBtn.IsEnabled = connectionsSelected;
            stopRecordBtn.IsEnabled = connectionsSelected;
            commandSendBtn.IsEnabled = connectionsSelected;

            deleteWatcherBtn.IsEnabled = cameraOperatorsSelected;

            msgSendBtn.IsEnabled = connectionsSelected;
            msgSendTeamBtn.IsEnabled = connectionsSelected;
            msgSendPlayerBtn.IsEnabled = playersSelected && connectionsSelected; // Sending to specific players.

            followBtn.IsEnabled = playersSelected && connectionsSelected; // Need to know who to follow and which connection to use
        }

        private void addCtfWatcherBtn_Click(object sender, RoutedEventArgs e)
        {
            createCameraOperator<CameraOperators.CTFCameraOperatorRedBlue>();
        }
        
        private void addOCDWatcherBtn_Click(object sender, RoutedEventArgs e)
        {
            createCameraOperator<CameraOperators.OCDCameraOperator>();
        }
        
        private void addSpectatorWatcherBtn_Click(object sender, RoutedEventArgs e)
        {
            createCameraOperator<CameraOperators.SpectatorCameraOperator>();
        }

        private void verbosityComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            int verbosity = 0;
            if( int.TryParse(((ComboBoxItem)verbosityComboBox.SelectedItem).Tag.ToString(), out verbosity))
            {
                verboseOutput = verbosity;
            }
        }

        private void connectionsDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            updateButtonEnablednesses();
        }

        private void deleteWatcherBtn_Click(object sender, RoutedEventArgs e)
        {
            List<CameraOperator> ops = cameraOperatorsDataGrid.SelectedItems.Cast<CameraOperator>().ToList();
            lock (cameraOperators)
            {
                foreach (CameraOperator op in ops)
                {
                    lock (connections)
                    {
                        op.Destroy();
                    }
                    cameraOperators.Remove(op);
                    updateIndices();
                    op.Errored -= CamOperator_Errored;
                }
            }
        }
        private void watcherConfigBtn_Click(object sender, RoutedEventArgs e)
        {
            List<CameraOperator> ops = cameraOperatorsDataGrid.SelectedItems.Cast<CameraOperator>().ToList();
            lock (cameraOperators)
            {
                foreach (CameraOperator op in ops)
                {
                    op.OpenDialog();
                }
            }
        }

        private void cameraOperatorsDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            updateButtonEnablednesses();
        }

        private void msgSendBtn_Click(object sender, RoutedEventArgs e)
        {
            List<Connection> conns = connectionsDataGrid.SelectedItems.Cast<Connection>().ToList();

            DoExecuteCommand("say \""+ commandLine.Text + "\"",conns.ToArray());
        }

        private void msgSendTeamBtn_Click(object sender, RoutedEventArgs e)
        {
            List<Connection> conns = connectionsDataGrid.SelectedItems.Cast<Connection>().ToList();

            DoExecuteCommand("say_team \"" + commandLine.Text + "\"", conns.ToArray());

        }

        private void msgSendPlayerBtn_Click(object sender, RoutedEventArgs e)
        {
            List<Connection> conns = connectionsDataGrid.SelectedItems.Cast<Connection>().ToList();
            List<PlayerInfo> recipients = playerListDataGrid.SelectedItems.Cast<PlayerInfo>().ToList();

            foreach(PlayerInfo recipient in recipients)
            {
                DoExecuteCommand("tell "+recipient.clientNum+" \"" + commandLine.Text + "\"", conns.ToArray());
            }
        }
        private void DoExecuteCommand(string command, Connection conn)
        {
            DoExecuteCommand(command, new Connection[] { conn });
        }

        private void DoExecuteCommand(string command, Connection[] conns)
        {
            foreach (Connection connection in conns)
            {
                if (connection.client.Status == ConnectionStatus.Active)
                {
                    //string command = commandLine.Text;
                    //commandLine.Text = "";
                    //connection.client.ExecuteCommand(command);
                    connection.leakyBucketRequester.requestExecution(command, RequestCategory.NONE, 1, 0, LeakyBucketRequester<string, RequestCategory>.RequestBehavior.ENQUEUE);

                    addToLog("[Conn "+connection.Index+", cN "+connection.client.clientNum+"] Command \"" + command + "\" sent.");
                }
            }
        }

        private void playerListDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            updateButtonEnablednesses();
        }

        private void followBtn_Click(object sender, RoutedEventArgs e)
        {
            List<Connection> conns = connectionsDataGrid.SelectedItems.Cast<Connection>().ToList();
            List<PlayerInfo> selectedPlayers = playerListDataGrid.SelectedItems.Cast<PlayerInfo>().ToList();

            if(selectedPlayers.Count > 1)
            {
                if(selectedPlayers.Count != conns.Count)
                {
                    addToLog("JKWatcher error: If you select more than one player to follow, the count of selected players must be equal to the count of selected connections. ("+ selectedPlayers.Count + " != "+ conns.Count + ")");
                    return;
                }
                for(int i = 0; i < selectedPlayers.Count; i++) // First selected connection follows first selected player, and so on
                {
                    DoExecuteCommand("follow " + selectedPlayers[i].clientNum, conns[0]);
                }
            } else if(selectedPlayers.Count > 0)
            {
                DoExecuteCommand("follow " + selectedPlayers[0].clientNum, conns.ToArray());
            }

            
        }

        private void checkDraw_Checked(object sender, RoutedEventArgs e)
        {
            DrawMiniMap = checkDraw.IsChecked.HasValue ? checkDraw.IsChecked.Value : false;
        }

        private void newConBtn_Click(object sender, RoutedEventArgs e)
        {
            //Connection newConnection = new Connection(connections[0].client.ServerInfo, this, infoPool);
            Connection newConnection = new Connection(netAddress,protocol, this, infoPool, password);
            lock (connections)
            {
                connections.Add(newConnection);
            }
            updateIndices();
        }


        private void delBtn_Click(object sender, RoutedEventArgs e)
        {
            /*if(connections.Count < 2) // We allow this now because we no longer have places relying on connections[0], specifically in places that create new connections.
            {
                addToLog("Cannot remove connections if only one connection exists.");
                return; // We won't delete our only remaining connection.
            }*/

            List<Connection> conns = connectionsDataGrid.SelectedItems.Cast<Connection>().ToList();

            /*if(connections.Count - conns.Count < 1)
            {
                addToLog("Cannot remove connections if none would be left.");
                return; // We won't delete all counnections. We want to keep at least one.
            }*/

            foreach (Connection conn in conns)
            {
                //if (conn.Status == ConnectionStatus.Active) // We wanna be able to delete faulty/disconnected connections too. Even more actually! If a connection gets stuck, it shouldn't stay there forever.
                //{
                    if(conn.CameraOperator != null)
                    {
                        addToLog("Cannot remove connection bound to a camera operator");
                    } else
                    {

                        //conn.disconnect();
                        conn.CloseDown();
                        connections.Remove(conn);
                    }
                //}
            }
        }

        private void reconBtn_Click(object sender, RoutedEventArgs e)
        {
            if (connections.Count == 0) return;

            // we make a copy of the selected items because otherwise the command might change something
            // that also results in a change of selecteditems and then it would only get the first item.
            List<Connection> conns = connectionsDataGrid.SelectedItems.Cast<Connection>().ToList();

            foreach (Connection conn in conns)
            {
                if (conn != null && conn.Status != ConnectionStatus.Active)
                {
                    //_=conn.hardReconnect();
                    _=conn.Reconnect();
                }
            }
        }


        public bool requestConnectionDestruction(Connection conn)
        {
            if (conn.CameraOperator != null)
            {
                addToLog("Cannot remove connection bound to a camera operator");
                return false;
            }
            else if (!connections.Contains(conn))
            {
                addToLog("WEIRD: Camera operator requesting deletion of a connection that is not part of this ConnectedServerWindow.");
                return false;
            }
            else
            {

                //conn.disconnect();
                conn.CloseDown();
                connections.Remove(conn);
                updateIndices();
                return true;
            }
        }
    }
}
