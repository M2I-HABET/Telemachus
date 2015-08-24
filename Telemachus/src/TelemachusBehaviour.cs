﻿//Author: Richard Bunt
using KSP.IO;
using Servers.MinimalHTTPServer;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Timers;
using UnityEngine;

namespace Telemachus
{
    class TelemachusBehaviour : MonoBehaviour
    {
        #region Constants

        private float MICRO_SECONDS = 1000.0f;

        #endregion

        #region Fields

        public static GameObject instance;
        private DelayedAPIRunner delayedAPIRunner = new DelayedAPIRunner();

        #endregion

        #region Data Link

        private static Server server = null;
        private static Servers.MinimalWebSocketServer.Server webSocketServer = null;
        private static PluginConfiguration config = PluginConfiguration.CreateForType<TelemachusBehaviour>();
        private static ServerConfiguration serverConfig = new ServerConfiguration();
        private static DataLinkResponsibility dataLinkResponsibility = null;
        private static IOPageResponsibility ioPageResponsibility = null;
        private static VesselChangeDetector vesselChangeDetector = null;
        private static KSPWebSocketService kspWebSocketService = null;
        private static IterationToEvent<UpdateTimerEventArgs> kspWebSocketDataStreamer = new IterationToEvent<UpdateTimerEventArgs>();

        // Create a default plugin manager to handle registrations
        private static PluginManager pluginManager = new PluginManager();

        private static bool isPartless = false;

        static public string getServerPrimaryIPAddress()
        {
            return serverConfig.ipAddresses[0].ToString();
        }

        static public string getServerPort()
        {
            return serverConfig.port.ToString();
        }

        static private void startDataLink()
        {
            if (server == null)
            {
                try
                {
                    PluginLogger.print("Telemachus data link starting");

                    readConfiguration();

                    server = new Server(serverConfig);
                    server.ServerNotify += HTTPServerNotify;
                    server.addHTTPResponsibility(new ElseResponsibility());
                    ioPageResponsibility = new IOPageResponsibility();
                    server.addHTTPResponsibility(ioPageResponsibility);

                    vesselChangeDetector = new VesselChangeDetector(isPartless);

                    dataLinkResponsibility = new DataLinkResponsibility(serverConfig, new KSPAPI(JSONFormatterProvider.Instance, vesselChangeDetector, serverConfig, pluginManager));
                    server.addHTTPResponsibility(dataLinkResponsibility);

                    Servers.MinimalWebSocketServer.ServerConfiguration webSocketconfig = new Servers.MinimalWebSocketServer.ServerConfiguration();
                    webSocketconfig.bufferSize = 300;
                    webSocketServer = new Servers.MinimalWebSocketServer.Server(webSocketconfig);
                    webSocketServer.ServerNotify += WebSocketServerNotify;
                    kspWebSocketService = new KSPWebSocketService(new KSPAPI(JSONFormatterProvider.Instance, vesselChangeDetector, serverConfig, pluginManager), 
                        kspWebSocketDataStreamer);
                    webSocketServer.addWebSocketService("/datalink", kspWebSocketService);
                    webSocketServer.subscribeToHTTPForStealing(server);

                    server.startServing();

                    PluginLogger.print("Telemachus data link listening for requests on the following addresses: ("
                        + server.getIPsAsString() +
                        "). Try putting them into your web browser, some of them might not work.");
                }
                catch (Exception e)
                {
                    PluginLogger.print(e.Message);
                    PluginLogger.print(e.StackTrace);
                }
            }
        }

        static private void writeDefaultConfig()
        {
            config.SetValue("PORT", 8085);
            config.SetValue("IPADDRESS", "127.0.0.1");
            config.save();
        }

        static private void readConfiguration()
        {
            config.load();

            int port = config.GetValue<int>("PORT");

            if (port != 0)
            {
                serverConfig.port = port;
            }
            else
            {
                PluginLogger.print("No port in configuration file.");
            }

            String ip = config.GetValue<String>("IPADDRESS");

            if (ip != null)
            {
                try
                {
                    serverConfig.addIPAddressAsString(ip);
                }
                catch
                {
                    PluginLogger.print("Invalid IP address in configuration file, falling back to find.");
                }
            }
            else
            {
                PluginLogger.print("No IP address in configuration file.");
            }


            serverConfig.maxRequestLength = config.GetValue<int>("MAXREQUESTLENGTH");

            if (serverConfig.maxRequestLength < 8000)
            {
                PluginLogger.print("No max request length specified, setting to 8000.");
                serverConfig.maxRequestLength = 10000;
            }
            else
            {
                PluginLogger.print("Max request length set to:" + serverConfig.maxRequestLength);
            }
            
            serverConfig.version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString();
            serverConfig.name = "Telemachus";
            serverConfig.backLog = 1000;

            isPartless = config.GetValue<int>("PARTLESS") == 0 ? false : true;
            PluginLogger.print("Partless:" + isPartless);
        }

        static private void stopDataLink()
        {
            if (server != null)
            {
                PluginLogger.print("Telemachus data link shutting down.");
                server.stopServing();
                server = null;
                webSocketServer.stopServing();
                webSocketServer = null;
            }
        }

        private static void HTTPServerNotify(object sender, Servers.NotifyEventArgs e)
        {
            PluginLogger.debug(e.message);
        }

        private static void WebSocketServerNotify(object sender, Servers.NotifyEventArgs e)
        {
            PluginLogger.debug(e.message);
        }

        #endregion

        #region Behaviour Events

        public void Awake()
        {
            LookForModsToInject();
            DontDestroyOnLoad(this);
            startDataLink();
        }

        public void OnDestroy()
        {
            stopDataLink();
        }

        public void Update()
        {
            delayedAPIRunner.execute();

            if (FlightGlobals.fetch != null)
            {
                vesselChangeDetector.update(FlightGlobals.ActiveVessel);
                kspWebSocketDataStreamer.update(new UpdateTimerEventArgs(Time.time * MICRO_SECONDS));
            }
            else
            {
                PluginLogger.debug("Flight globals was null during start up; skipping update of vessel change.");
            }
        }


        void LookForModsToInject()
        {
            string foundMods = "Loading; Looking for compatible mods to inject registration....\nTelemachus compatible modules Found:\n";
            int found = 0;
            foreach (var asm in AssemblyLoader.loadedAssemblies)
            {
                foreach (var type in asm.assembly.GetTypes())
                {
                    if (type.IsSubclassOf(typeof(MonoBehaviour)))
                    {
                        // Does this have a static property named "Func<string> TelemachusPluginRegister { get; set; }?
                        var prop = type.GetProperty("TelemachusPluginRegister", BindingFlags.Static | BindingFlags.Public);
                        if (prop == null) continue;
                        found += 1;
                        foundMods += "  - " + type.ToString() + " ";
                        if (prop.PropertyType != typeof(Action<object>))
                        {
                            foundMods += "(Fail - Invalid property type)\n";
                            continue;
                        }

                        if (!prop.CanWrite)
                        {
                            foundMods += "(Fail - Property not writeable)\n";
                            continue;
                        }
                        // Can we read it - if so, only write if it is not null.
                        if (prop.CanRead)
                        {
                            if (prop.GetValue(null, null) != null)
                            {
                                foundMods += "(Fail - Property not null)\n";
                                continue;
                            }
                        }
                        // Write the value here
                        Action<object> pluginRegister = PluginRegistration.Register;
                        prop.SetValue(null, pluginRegister, null);
                        foundMods += "(Success)\n";
                    }
                }
            }
            if (found == 0) foundMods += "  None";
            PluginLogger.print(foundMods);
        }
        #endregion

        #region DataRate

        static public double getDownLinkRate()
        {
            return dataLinkResponsibility.dataRates.getDownLinkRate() + KSPWebSocketService.dataRates.getDownLinkRate();
        }

        static public double getUpLinkRate()
        {
            return dataLinkResponsibility.dataRates.getUpLinkRate() + KSPWebSocketService.dataRates.getUpLinkRate();
        }

        #endregion

        #region Delayed API Runner

        public void queueDelayedAPI(DelayedAPIEntry entry)
        {
            delayedAPIRunner.queue(entry);
        }

        #endregion
    }

    public class DelayedAPIRunner
    {
        #region Fields

        List<DelayedAPIEntry> actionQueue = new List<DelayedAPIEntry>();

        #endregion

        #region Lock

        readonly private System.Object queueLock = new System.Object();

        #endregion

        #region Methods

        public void execute()
        {
            lock (queueLock)
            {
                foreach (DelayedAPIEntry entry in actionQueue)
                {
                    entry.call();
                }

                actionQueue.Clear();
            }
        }

        public void queue(DelayedAPIEntry delayedAPIEntry)
        {
            lock (queueLock)
            {
                actionQueue.Add(delayedAPIEntry);
            }
        }

        #endregion
    }

    public class KSPAPI : IKSPAPI
    {
        private PluginManager _manager;

        public KSPAPI(FormatterProvider formatters, VesselChangeDetector vesselChangeDetector,
            Servers.AsynchronousServer.ServerConfiguration serverConfiguration, PluginManager manager)
        {
            _manager = manager;

            APIHandlers.Add(new PausedDataLinkHandler(formatters));
            APIHandlers.Add(new FlyByWireDataLinkHandler(formatters));
            APIHandlers.Add(new FlightDataLinkHandler(formatters));
            APIHandlers.Add(new MechJebDataLinkHandler(formatters));
            APIHandlers.Add(new TimeWarpDataLinkHandler(formatters));
            APIHandlers.Add(new TargetDataLinkHandler(formatters));

            APIHandlers.Add(new CompoundDataLinkHandler(
                new List<DataLinkHandler>() { 
                    new OrbitDataLinkHandler(formatters),
                    new SensorDataLinkHandler(vesselChangeDetector, formatters),
                    new VesselDataLinkHandler(formatters),
                    new BodyDataLinkHandler(formatters),
                    new ResourceDataLinkHandler(vesselChangeDetector, formatters),
                    new APIDataLinkHandler(this, formatters, serverConfiguration),
                    new NavBallDataLinkHandler(formatters),
                    new MapViewDataLinkHandler(formatters),
                    new DockingDataLinkHandler(formatters)
                    }, formatters
                ));
        }

        public override Vessel getVessel()
        {
            return FlightGlobals.ActiveVessel;
        }

        public override object ProcessAPIString(string apistring)
        {
            var data = new DataSources() { vessel = getVessel() };
            // Extract any arguments/parameters in this API string
            var name = apistring;
            parseParams(ref name, ref data);

            try {
                // Get the API entry
                APIEntry apiEntry = null;
                process(name, out apiEntry);
                if (apiEntry == null) return null;

                // run the API entry
                var result = apiEntry.function(data);
                // And return the serialization-ready value
                return apiEntry.formatter.prepareForSerialization(result);
            } catch (UnknownAPIException)
            {
                PluginLogger.print("No entry internally: Looking at plugins");
                // Try looking in the pluginManager
                var pluginAPI = _manager.GetAPIDelegate(name);
                // If no entry, just continue the throwing of the exception
                if (pluginAPI == null) throw;
                // We found an API entry! Let's use that.
                return pluginAPI(data.vessel, data.args.ToArray());
            }
        }
    }

    public abstract class IKSPAPI
    {
        public class UnknownAPIException : ArgumentException
        {
            public string apiString = "";

            public UnknownAPIException(string apiString = "")
            {
                this.apiString = apiString;
            }

            public UnknownAPIException(string message, string apiString = "")
                : base(message)
            {
                this.apiString = apiString;
            }

            public UnknownAPIException(string message, string apiString, Exception inner)
                : base(message, inner)
            {
                this.apiString = apiString;
            }
        }


        protected List<DataLinkHandler> APIHandlers = new List<DataLinkHandler>();

        public void getAPIList(ref List<APIEntry> APIList)
        {
            foreach (DataLinkHandler APIHandler in APIHandlers)
            {
                APIHandler.appendAPIList(ref APIList);
            }
        }

        public void getAPIEntry(string APIString, ref List<APIEntry> APIList)
        {
            APIEntry result = null;

            foreach (DataLinkHandler APIHandler in APIHandlers)
            {
                if (APIHandler.process(APIString, out result))
                {
                    break;
                }
            }

            APIList.Add(result);
        }

        public void process(String API, out APIEntry apiEntry)
        {
            APIEntry result = null;
            foreach (DataLinkHandler APIHandler in APIHandlers)
            {
                if (APIHandler.process(API, out result))
                {
                    break;
                }
            }
            if (result == null) throw new UnknownAPIException("Could not find API entry named " + API, API);
            apiEntry = result;
        }

        abstract public Vessel getVessel();

        public void parseParams(ref String arg, ref DataSources dataSources)
        {
            dataSources.args.Clear();

            try
            {
                if (arg.Contains("["))
                {
                    String[] argsSplit = arg.Split('[');
                    argsSplit[1] = argsSplit[1].Substring(0, argsSplit[1].Length - 1);
                    arg = argsSplit[0];
                    String[] paramSplit = argsSplit[1].Split(',');

                    for (int i = 0; i < paramSplit.Length; i++)
                    {
                        dataSources.args.Add(paramSplit[i]);
                    }
                }
            }
            catch (Exception e)
            {
                PluginLogger.debug(e.Message + " " + e.StackTrace);
            }
        }

        /// <summary>
        /// Accepts a string, and does any API processing (with the current vessel), returning the result.
        /// </summary>
        /// <remarks>This take responsibility for the whole chain of parsing, splitting and searching for the API</remarks>
        /// <param name="apistring"></param>
        /// <returns></returns>
        public abstract object ProcessAPIString(string apistring);
    }
}
