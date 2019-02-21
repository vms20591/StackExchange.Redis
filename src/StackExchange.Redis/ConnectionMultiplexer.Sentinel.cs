using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StackExchange.Redis
{
    public partial class ConnectionMultiplexer
    {
        internal EndPoint currentSentinelMasterEndPoint = null;

        internal System.Threading.Timer sentinelMasterReconnectTimer = null;

        internal Dictionary<string, ConnectionMultiplexer> sentinelConnectionChildren = null;

        /// <summary>
        /// Initializes the connection as a Sentinel connection and adds
        /// the necessary event handlers to track changes to the managed
        /// masters.
        /// </summary>
        /// <param name="log"></param>
        private void InitializeSentinel(TextWriter log)
        {
            if (ServerSelectionStrategy.ServerType != ServerType.Sentinel)
                return;

            sentinelConnectionChildren = new Dictionary<string, ConnectionMultiplexer>();

            InitializeSubscriptions();

            // If we lose connection to a sentinel server,
            // We need to reconfigure to make sure we still have
            // a subscription to the +switch-master channel.
            this.ConnectionFailed += (sender, e) => {
                // Reconfigure to get subscriptions back online
                ReconfigureAsync(false, true, log, e.EndPoint, "Lost sentinel connection", false).Wait();
            };            
        }

        private Dictionary<string, Action<RedisChannel, RedisValue>> _subscriptionHandlers = null;
        private Dictionary<string, Action<RedisChannel, RedisValue>> SubscriptionHandlers
        {
            get
            {
                if (_subscriptionHandlers == null)
                {
                    _subscriptionHandlers = new Dictionary<string, Action<RedisChannel, RedisValue>>
                    {
                        { "+switch-master", OnSwitchMaster },
                        { "+sentinel", OnAddSentinal },
                        { "-dup-sentinel", OnDuplicateSentinel },
                    };
                }

                return _subscriptionHandlers;
            }
        }

        private void InitializeSubscriptions()
        {
            // Subscribe to sentinel change events
            ISubscriber sub = GetSubscriber();

            Task[] tasks = SubscriptionHandlers
                .Where(x => sub.SubscribedEndpoint(x.Key) == null)
                .Select(x => sub.SubscribeAsync(x.Key, x.Value))
                .ToArray();

            Task.WaitAll(tasks);
        }

        private void OnSwitchMaster(RedisChannel channel, RedisValue message)
        {
            string[] messageParts = ((string)message).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            EndPoint switchBlame = Format.TryParseEndPoint(string.Format("{0}:{1}", messageParts[1], messageParts[2]));

            lock (sentinelConnectionChildren)
            {
                // Switch the master if we have connections for that service
                if (sentinelConnectionChildren.ContainsKey(messageParts[0]))
                {
                    ConnectionMultiplexer child = sentinelConnectionChildren[messageParts[0]];

                    // Is the connection still valid?
                    if (child.IsDisposed)
                    {
                        child.ConnectionFailed -= OnManagedConnectionFailed;
                        child.ConnectionRestored -= OnManagedConnectionRestored;
                        sentinelConnectionChildren.Remove(messageParts[0]);
                    }
                    else
                    {
                        SwitchMaster(switchBlame, sentinelConnectionChildren[messageParts[0]]);
                    }
                }
            }
        }

        private void OnAddSentinal(RedisChannel channel, RedisValue message)
        {
            string[] messageParts = ((string)message).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            UpdateSentinelAddressList(messageParts[0]);
        }

        private void OnDuplicateSentinel(RedisChannel channel, RedisValue message)
        {
            string[] messageParts = ((string)message).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            UpdateSentinelAddressList(messageParts[0]);
        }

        /// <summary>
        /// Returns a managed connection to the master server indicated by
        /// the ServiceName in the config.
        /// </summary>
        /// <param name="config">the configuration to be used when connecting to the master</param>
        /// <param name="log"></param>
        /// <returns></returns>
        public ConnectionMultiplexer GetSentinelMasterConnection(ConfigurationOptions config, TextWriter log = null)
        {
            if (ServerSelectionStrategy.ServerType != ServerType.Sentinel)
                throw new NotImplementedException("The ConnectionMultiplexer is not a Sentinel connection.");

            if (String.IsNullOrEmpty(config.ServiceName))
                throw new ArgumentException("A ServiceName must be specified.");

            lock (sentinelConnectionChildren)
            {
                if (sentinelConnectionChildren.ContainsKey(config.ServiceName) && !sentinelConnectionChildren[config.ServiceName].IsDisposed)
                    return sentinelConnectionChildren[config.ServiceName];
            }

            // Clear out the endpoints                        
            config.EndPoints.Clear();

            // Get an initial endpoint
            EndPoint initialMasterEndPoint = null;

            do
            {
                initialMasterEndPoint = GetConfiguredMasterForService(config.ServiceName);
            } while (initialMasterEndPoint == null);

            config.EndPoints.Add(initialMasterEndPoint);

            ConnectionMultiplexer connection = ConnectionMultiplexer.Connect(config, log);

            // Attach to reconnect event to ensure proper connection to the new master
            connection.ConnectionRestored += OnManagedConnectionRestored;

            // If we lost the connection, run a switch to a least try and get updated info about the master
            connection.ConnectionFailed += OnManagedConnectionFailed;

            lock (sentinelConnectionChildren)
            {
                sentinelConnectionChildren[connection.RawConfig.ServiceName] = connection;
            }

            // Perform the initial switchover
            SwitchMaster(RawConfig.EndPoints[0], connection, log);

            return connection;
        }

        internal void OnManagedConnectionRestored(Object sender, ConnectionFailedEventArgs e)
        {
            ConnectionMultiplexer connection = (ConnectionMultiplexer)sender;

            if (connection.sentinelMasterReconnectTimer != null)
            {
                connection.sentinelMasterReconnectTimer.Dispose();
                connection.sentinelMasterReconnectTimer = null;
            }

            // Run a switch to make sure we have update-to-date 
            // information about which master we should connect to
            SwitchMaster(e.EndPoint, connection);

            try
            {
                // Verify that the reconnected endpoint is a master,
                // and the correct one otherwise we should reconnect
                if (connection.GetServer(e.EndPoint).IsSlave || e.EndPoint != connection.currentSentinelMasterEndPoint)
                {
                    // Wait for things to smooth out
                    Thread.Sleep(200);

                    // This isn't a master, so try connecting again
                    SwitchMaster(e.EndPoint, connection);
                }
            }
            catch (Exception)
            {
                // If we get here it means that we tried to reconnect to a server that is no longer
                // considered a master by Sentinel and was removed from the list of endpoints.

                // Wait for things to smooth out
                Thread.Sleep(200);

                // If we caught an exception, we may have gotten a stale endpoint
                // we are not aware of, so retry
                SwitchMaster(e.EndPoint, connection);
            }
        }

        internal void OnManagedConnectionFailed(Object sender, ConnectionFailedEventArgs e)
        {
            ConnectionMultiplexer connection = (ConnectionMultiplexer)sender;
            // Periodically check to see if we can reconnect to the proper master.
            // This is here in case we lost our subscription to a good sentinel instance
            // or if we miss the published master change
            if (connection.sentinelMasterReconnectTimer == null)
            {
                connection.sentinelMasterReconnectTimer = new System.Threading.Timer((o) => {
                    SwitchMaster(e.EndPoint, connection);
                }, null, TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(1));

                //connection.sentinelMasterReconnectTimer.AutoReset = true;                            

                //connection.sentinelMasterReconnectTimer.Start();
            }
        }

        internal EndPoint GetConfiguredMasterForService(String serviceName, int timeoutmillis = -1)
        {
            Task<EndPoint>[] sentinelMasters = _serverSnapshot.Span
                .ToArray()
                .Where(s => s.ServerType == ServerType.Sentinel)
                .Select(s => GetServer(s.EndPoint).SentinelGetMasterAddressByNameAsync(serviceName))
                .ToArray();

            Task<Task<EndPoint>> firstCompleteRequest = WaitFirstNonNullIgnoreErrorsAsync(sentinelMasters);

            if (!firstCompleteRequest.Wait(timeoutmillis))
                throw new TimeoutException("Timeout resolving master for service");
            if (firstCompleteRequest.Result.Result == null)
                throw new Exception("Unable to determine master");

            return firstCompleteRequest.Result.Result;
        }

        private static async Task<Task<T>> WaitFirstNonNullIgnoreErrorsAsync<T>(Task<T>[] tasks)
        {
            if (tasks == null) throw new ArgumentNullException("tasks");
            if (tasks.Length == 0) return null;
            var typeNullable = (Nullable.GetUnderlyingType(typeof(T)) != null);
            var taskList = tasks.Cast<Task>().ToList();

            try
            {
                while (taskList.Count() > 0)
                {
#if NET40
                    var allTasksAwaitingAny = TaskEx.WhenAny(taskList).ObserveErrors();
#else
                    var allTasksAwaitingAny = Task.WhenAny(taskList).ObserveErrors();
#endif
                    var result = await allTasksAwaitingAny.ForAwait();
                    taskList.Remove((Task<T>)result);
                    if (((Task<T>)result).IsFaulted) continue;
                    if ((!typeNullable) || ((Task<T>)result).Result != null)
                        return (Task<T>)result;
                }
            }
            catch
            { }

            return null;
        }

        /// <summary>
        ///  Switches the SentinelMasterConnection over to a new master.
        /// </summary>
        /// <param name="switchBlame">the endpoing responsible for the switch</param>
        /// <param name="connection">the connection that should be switched over to a new master endpoint</param>
        /// <param name="log">log output</param>
        internal void SwitchMaster(EndPoint switchBlame, ConnectionMultiplexer connection, TextWriter log = null)
        {
            if (log == null) log = TextWriter.Null;

            String serviceName = connection.RawConfig.ServiceName;

            // Get new master
            EndPoint masterEndPoint = null;

            do
            {
                masterEndPoint = GetConfiguredMasterForService(serviceName);
            } while (masterEndPoint == null);

            connection.currentSentinelMasterEndPoint = masterEndPoint;

            if (!connection.servers.Contains(masterEndPoint))
            {
                connection.RawConfig.EndPoints.Clear();
                connection.servers.Clear();
                connection._serverSnapshot = ServerSnapshot.Empty;
                connection.RawConfig.EndPoints.Add(masterEndPoint);
                Trace(string.Format("Switching master to {0}", masterEndPoint));
                // Trigger a reconfigure                            
                connection.ReconfigureAsync(false, false, log, switchBlame, string.Format("master switch {0}", serviceName), false, CommandFlags.PreferMaster).Wait();
            }

            UpdateSentinelAddressList(serviceName);
        }

        internal void UpdateSentinelAddressList(String serviceName, int timeoutmillis = 500)
        {
            Task<EndPoint[]>[] sentinels = _serverSnapshot.Span
                .ToArray()
                .Where(s => s.ServerType == ServerType.Sentinel)
                .Select(s =>
                {
                    return GetServer(s.EndPoint).SentinelSentinelsAsync(serviceName)
                    .ContinueWith((task) =>
                    {
                        return task.Result
                            .Select(x => x.ToDictionary())
                            .Where(x => x.ContainsKey("ip") && x.ContainsKey("port") && int.TryParse(x["port"], out int portInt))
                            .Select(x => Format.ParseEndPoint(x["ip"], int.Parse(x["port"])))
                            .ToArray();
                    });
                })
                .ToArray();

            Task<Task<EndPoint[]>> firstCompleteRequest = WaitFirstNonNullIgnoreErrorsAsync(sentinels);

            // Ignore errors, as having an updated sentinel list is
            // not essential
            if (firstCompleteRequest.Result?.Result == null)
                return;
            if (!firstCompleteRequest.Wait(timeoutmillis))
                return;
            if (firstCompleteRequest.Result.Result == null)
                return;

            bool hasNew = false;
            foreach (EndPoint newSentinel in firstCompleteRequest.Result.Result.Where(x => !RawConfig.EndPoints.Contains(x)))
            {
                hasNew = true;
                RawConfig.EndPoints.Add(newSentinel);
            }

            if (hasNew)
            {
                // Reconfigure the sentinel multiplexer if we added new endpoints
                ReconfigureAsync(false, true, null, RawConfig.EndPoints[0], "Updating Sentinel List", false).Wait();
            }
        }
    }
}
