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

namespace JKWatcher
{
    /// <summary>
    /// Interaction logic for ConnectedServerWindow.xaml
    /// </summary>
    public partial class ConnectedServerWindow : Window
    {

        private ServerInfo serverInfo;

        private FullyObservableCollection<Connection> connections = new FullyObservableCollection<Connection>();
        private FullyObservableCollection<CameraOperator> cameraOperators = new FullyObservableCollection<CameraOperator>();

        private const int maxLogLength = 10000;
        private string logString = "Begin of Log\n";

        private ServerSharedInformationPool infoPool;

        private List<CancellationTokenSource> backgroundTasks = new List<CancellationTokenSource>();

        public bool verboseOutput { get; private set; } = false;

        // TODO: Send "score" command to server every second or 2 so we always have up to date scoreboards. will eat a bit more space maybe but should be cool. make it possible to disable this via some option, or to set interval

        public ConnectedServerWindow(ServerInfo serverInfoA)
        {
            serverInfo = serverInfoA;
            InitializeComponent();
            this.Title = serverInfo.Address.ToString() + " (" + serverInfo.HostName + ")";

            connectionsDataGrid.ItemsSource = connections;
            cameraOperatorsDataGrid.ItemsSource = cameraOperators;

            infoPool = new ServerSharedInformationPool();

            connections.Add(new Connection(this,serverInfo, infoPool));

            logTxt.Text = logString;

            this.Closed += ConnectedServerWindow_Closed;

            var tokenSource = new CancellationTokenSource();
            CancellationToken ct = tokenSource.Token;
            Task.Factory.StartNew(() => { miniMapUpdater(ct); }, ct, TaskCreationOptions.LongRunning,TaskScheduler.Default);
            backgroundTasks.Add(tokenSource);

            tokenSource = new CancellationTokenSource();
            ct = tokenSource.Token;
            Task.Factory.StartNew(() => { scoreBoardRequester(ct); }, ct, TaskCreationOptions.LongRunning,TaskScheduler.Default);
            backgroundTasks.Add(tokenSource);
        }

        private unsafe void scoreBoardRequester(CancellationToken ct)
        {
            while (true)
            {
                System.Threading.Thread.Sleep(1000);
                ct.ThrowIfCancellationRequested();

                foreach(Connection connection in connections)
                {
                    if(connection.client.Status == ConnectionStatus.Active)
                    {
                        connection.client.ExecuteCommand("score");
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
                
                for(int i = 0; i < JKClient.Common.MaxClients; i++)
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
                for (int i = 0; i < JKClient.Common.MaxClients; i++)
                {
                    if(infoPool.playerInfo[i].lastPositionUpdate == null)
                    {
                        continue; // don't have any position data
                    }
                    if ((DateTime.Now - infoPool.playerInfo[i].lastPositionUpdate.Value).TotalSeconds > miniMapOutdatedDrawTime)
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

        private void createCameraOperator<T>() where T:CameraOperator, new()
        {
            T camOperator = new T();
            int requiredConnectionCount = camOperator.getRequiredConnectionCount();
            Connection[] connectionsForCamOperator = getUnboundConnections(requiredConnectionCount);
            camOperator.provideConnections(connectionsForCamOperator);
            camOperator.provideServerSharedInformationPool(infoPool);
            camOperator.Initialize();
            cameraOperators.Add(camOperator);
        }

        private Connection[] getUnboundConnections(int count)
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
                Connection newConnection = new Connection(this,connections[0].client.ServerInfo,infoPool);
                connections.Add(newConnection);
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

            if (connections.Count == 0) return;

            foreach (Connection connection in connections)
            {
                connection.stopDemoRecord();
                connection.disconnect();
                break;
            }
        }

        public void addToLog(string someString)
        {
            lock (logString) { 
                someString += "\n";
                int newLogLength = logString.Length + someString.Length;
                if (newLogLength <= maxLogLength)
                {
                    logString += someString;
                } else
                {
                    int cutAway = newLogLength -maxLogLength;
                    logString = logString.Substring(cutAway) + someString;
                }
                Dispatcher.Invoke(()=> {

                    logTxt.Text = logString;
                });
            }
        }

        
        private void recordBtn_Click(object sender, RoutedEventArgs e)
        {
            if (connections.Count == 0) return;

            foreach(Connection connection in connections)
            {
                if(connection.client.Status == ConnectionStatus.Active)
                {

                    connection.startDemoRecord();
                    break;
                }
            }
        }

        private void stopRecordBtn_Click(object sender, RoutedEventArgs e)
        {
            if (connections.Count == 0) return;

            foreach (Connection connection in connections)
            {
                if (connection.client.Demorecording)
                {

                    connection.stopDemoRecord();
                    break;
                }
            }
        }

        private void commandSendBtn_Click(object sender, RoutedEventArgs e)
        {
            foreach (Connection connection in connections)
            {
                if (connection.client.Status == ConnectionStatus.Active)
                {
                    string command = commandLine.Text;
                    commandLine.Text = "";
                    connection.client.ExecuteCommand(command);

                    break;
                }
            }
        }

        private void addCtfWatcherBtn_Click(object sender, RoutedEventArgs e)
        {
            createCameraOperator<CameraOperators.CTFCameraOperatorRedBlue>();
        }

        private void verboseOutputCheck_Checked(object sender, RoutedEventArgs e)
        {
            verboseOutput = true;
        }

        private void verboseOutputCheck_Unchecked(object sender, RoutedEventArgs e)
        {

            verboseOutput = false;
        }
    }
}
