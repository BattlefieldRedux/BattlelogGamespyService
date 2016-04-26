﻿using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using BattleSpy.Gamespy;

namespace Server
{
    /// <summary>
    /// This server emulates the Gamespy Client Manager Server on port 29900.
    /// This class is responsible for managing the login process.
    /// </summary>
    public class GpcmServer : GamespyTcpSocket
    {
        /// <summary>
        /// Max number of concurrent open and active connections.
        /// </summary>
        /// <remarks>
        ///   Connections to the Gpcm server are active for the entire duration
        ///   that a client is logged in. Max conenctions is essentially the max number 
        ///   of players that can be logged in at the same time.
        /// </remarks>
        public static readonly int MaxConnections = Config.GetType<int>("Settings", "LoginServerMaxActiveClients");

        /// <summary>
        /// Indicates the timeout of when a connecting client will be disconnected
        /// </summary>
        public const int Timeout = 20000;

        /// <summary>
        /// A connection counter, used to create unique connection id's
        /// </summary>
        private long ConnectionCounter = 0;

        /// <summary>
        /// List of processing connections (id => Client Obj)
        /// </summary>
        private static ConcurrentDictionary<long, GpcmClient> Processing = new ConcurrentDictionary<long, GpcmClient>();

        /// <summary>
        /// List of sucessfully logged in clients (Pid => Client Obj)
        /// </summary>
        private static ConcurrentDictionary<long, GpcmClient> Clients = new ConcurrentDictionary<long, GpcmClient>();

        /// <summary>
        /// Returns a list of all the connected clients
        /// </summary>
        public GpcmClient[] ConnectedClients
        {
            get { return Clients.Values.ToArray(); }
        }

        /// <summary>
        /// Returns the number of connected clients
        /// </summary>
        /// <returns></returns>
        public int NumClients
        {
            get { return Clients.Count; }
        }

        /// <summary>
        /// A timer that is used to Poll all connections, and removes dropped connections
        /// </summary>
        public static System.Timers.Timer PollTimer { get; protected set; }

        public GpcmServer(IPEndPoint bindTo) : base(bindTo, MaxConnections)
        {
            // Register for events
            GpcmClient.OnSuccessfulLogin += GpcmClient_OnSuccessfulLogin;
            GpcmClient.OnDisconnect += GpcmClient_OnDisconnect;

            // Setup timer. Every 15 seconds should be sufficient
            PollTimer = new System.Timers.Timer(15000);
            PollTimer.Elapsed += (s, e) =>
            {
                // Send keep alive to all connected clients
                if (Clients.Count > 0)
                    Parallel.ForEach(Clients.Values, client => client.SendKeepAlive());

                // Disconnect hanging connections
                if (Processing.Count > 0)
                    Parallel.ForEach(Processing.Values, client => CheckTimeout(client));
            };
            PollTimer.Start();

            // Set connection handling
            base.ConnectionEnforceMode = EnforceMode.DuringPrepare;
            base.FullErrorMessage = Config.GetValue("Settings", "LoginServerFullMessage").Replace("\"", "");

            // Begin accepting connections
            base.StartAcceptAsync();
        }

        /// <summary>
        /// Shutsdown the ClientManager server and socket
        /// </summary>
        public void Shutdown()
        {
            // Stop accepting new connections
            base.IgnoreNewConnections = true;

            // Discard the poll timer
            PollTimer.Stop();
            PollTimer.Dispose();

            // Unregister events so we dont get a shit ton of calls
            GpcmClient.OnSuccessfulLogin -= GpcmClient_OnSuccessfulLogin;
            GpcmClient.OnDisconnect -= GpcmClient_OnDisconnect;

            // Disconnected all connected clients
            Console.WriteLine("Disconnecting all users...");
            Parallel.ForEach(Clients.Values, client => client.Disconnect(9));
            Parallel.ForEach(Processing.Values, client => client.Disconnect(9));

            // Update the database
            try
            {
                // Set everyone's online session to 0
                using (Database.GamespyDatabase Conn = new Database.GamespyDatabase())
                    Conn.Execute("UPDATE web_users SET game_session=0 WHERE game_sessions != 0");
            }
            catch (Exception e)
            {
                Program.ErrorLog.Write("WARNING: [GpcmServer.Shutdown] Failed to update client database: " + e.Message);
            }

            // Update Connected Clients in the Database
            Clients.Clear();

            // Shutdown the listener socket
            base.ShutdownSocket();

            // Tell the base to dispose all free objects
            base.Dispose();
        }

        /// <summary>
        /// When a new connection is established, we the parent class are responsible
        /// for handling the processing
        /// </summary>
        /// <param name="Stream">A GamespyTcpStream object that wraps the I/O AsyncEventArgs and socket</param>
        protected override void ProcessAccept(GamespyTcpStream Stream)
        {
            // Get our connection id
            long ConID = Interlocked.Increment(ref ConnectionCounter);
            GpcmClient client;

            try
            {
                // Create a new GpcmClient, passing the IO object for the TcpClientStream
                client = new GpcmClient(Stream, ConID);
                Processing.TryAdd(ConID, client);

                // Begin the asynchronous login process
                client.SendServerChallenge();
            }
            catch (Exception e)
            {
                // Log the error
                Program.ErrorLog.Write("WARNING: An Error occured at [GpcmServer.ProcessAccept] : Generating Exception Log");
                ExceptionHandler.GenerateExceptionLog(e);

                // Remove pending connection
                Processing.TryRemove(ConID, out client);

                // Release this stream so it can be used again
                base.Release(Stream);
            }
        }

        /// <summary>
        /// Checks the timeout on a client connection. This method is used to detect hanging connections, and
        /// forcefully disconnects them.
        /// </summary>
        /// <param name="client"></param>
        protected void CheckTimeout(GpcmClient client)
        {
            // Setup vars
            DateTime expireTime = client.Created.AddSeconds(Timeout);
            GpcmClient oldC;

            // Remove all processing connections that are hanging
            if (client.Status != LoginStatus.Completed && expireTime <= DateTime.Now)
            {
                try
                {
                    client.Disconnect(1);
                    Processing.TryRemove(client.ConnectionId, out oldC);
                }
                catch (Exception ex)
                {
                    // Log the error
                    Program.ErrorLog.Write(
                        "NOTICE: [GpcmServer.CheckTimeout] Error removing client from processing queue. Generating Excpetion Log"
                    );
                    ExceptionHandler.GenerateExceptionLog(ex);
                }
            }
            else if (client.Status == LoginStatus.Completed)
            {
                Processing.TryRemove(client.ConnectionId, out oldC);
            }
        }

        /// <summary>
        /// Returns whether the specified player is currently connected
        /// </summary>
        /// <param name="Pid">The players ID</param>
        /// <returns></returns>
        public bool IsConnected(int Pid)
        {
            return Clients.ContainsKey(Pid);
        }

        /// <summary>
        /// Forces the logout of a connected client
        /// </summary>
        /// <param name="Pid">The players ID</param>
        /// <returns>Returns whether the client was connected, and disconnect was called</returns>
        public bool ForceLogout(int Pid)
        {
            if (Clients.ContainsKey(Pid))
            {
                Clients[Pid].Disconnect(1);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Callback for when a connection had disconnected
        /// </summary>
        /// <param name="client">The client object whom is disconnecting</param>
        private void GpcmClient_OnDisconnect(GpcmClient client)
        {
            // Remove client, and call OnUpdate Event
            try
            {
                // Remove client from online list
                if (Clients.TryRemove(client.PlayerId, out client) && !client.Disposed)
                    client.Dispose();
            }
            catch (Exception e)
            {
                Program.ErrorLog.Write("An Error occured at [GpcmServer._OnDisconnect] : Generating Exception Log");
                ExceptionHandler.GenerateExceptionLog(e);
            }
        }

        /// <summary>
        /// Callback for a successful login
        /// </summary>
        /// <param name="sender">The GpcmClient that is logged in</param>
        private void GpcmClient_OnSuccessfulLogin(object sender)
        {
            // Wrap this in a try/catch
            try
            {
                GpcmClient oldC;
                GpcmClient client = sender as GpcmClient;

                // Check to see if the client is already logged in, if so disconnect the old user
                if (Clients.TryRemove(client.PlayerId, out oldC))
                {
                    oldC.Disconnect(1);
                    return;
                }

                // Remove connection from processing
                Processing.TryRemove(client.ConnectionId, out oldC);

                // Add current client to the dictionary
                if (!Clients.TryAdd(client.PlayerId, client))
                {
                    Program.ErrorLog.Write("ERROR: [GpcmServer._OnSuccessfulLogin] Unable to add client to HashSet.");
                    return;
                }
            }
            catch (Exception E)
            {
                Program.ErrorLog.Write("ERROR: [GpcmServer._OnSuccessfulLogin] Exception was thrown, Generating exception log.");
                ExceptionHandler.GenerateExceptionLog(E);
            }
        }

        protected override void OnException(Exception e)
        {
            ExceptionHandler.GenerateExceptionLog(e);
        }
    }
}
