// Based on https://github.com/EnlightenedOne/MirrorNetworkDiscovery
// forked from https://github.com/in0finite/MirrorNetworkDiscovery
// Both are MIT Licensed

// Updated 2022-02-20 by Coburn (SoftwareGuy)
// This update has changes integrated from the Mirror PR #2887
// PR author: Clancey; 22 Aug 2021
// Source: https://github.com/vis2k/Mirror/pull/2887/

namespace Mirage.Discovery
{
    using System;
    using System.Net;
    using System.Net.Sockets;
    using System.Text;
    using System.Threading.Tasks;
    using Cysharp.Threading.Tasks;
    using Logging;
    using Serialization;
    using UnityEngine;
    using UnityEngine.Rendering;

    /// <summary>
    /// Base implementation of server discovery on the local network. Extend this component to provide custom discovery with game specific data.
    /// </summary>
    /// <typeparam name="TRequest">Request message type. Must be a struct marked with <see cref="NetworkMessageAttribute"/>.</typeparam>
    /// <typeparam name="TResponse">Response message type. Must be a struct marked with <see cref="NetworkMessageAttribute"/>.</typeparam>
    /// <remarks>
    /// See <see cref="LanDiscovery"/> for a sample implementation.
    /// </remarks>
    [DisallowMultipleComponent]
    [HelpURL("https://miragenet.github.io/Mirage/Articles/Components/LanDiscovery.html")]
    public abstract class LanDiscoveryBase<TRequest, TResponse> : MonoBehaviour
    {
        #region Variables / Properties

        private static readonly ILogger Logger = LogFactory.GetLogger(typeof(LanDiscoveryBase<TRequest, TResponse>));

        public static bool IsSupportedOnThisPlatform => Application.platform != RuntimePlatform.WebGLPlayer;

        /// <summary>
        /// Unique 64-bit integer identifier of the application. Used as a handshake to match the instances of the same app when doing network discovery.
        /// Generated automatically by the underlying NetworkDiscovery implementation.
        /// </summary>
        [ReadOnlyInspector]
        [Header("Base Discovery Settings")]
        [Tooltip("Unique identifier of the application. Used to match the instances of the same app when doing network discovery. Generated automatically by the underlying LanDiscovery implementation.")]
        public long uniqueAppIdentifier;

        [SerializeField]
        [Tooltip("The UDP port the server will listen for multi-cast messages")]
        protected int serverBroadcastListenPort = 47777;

        [SerializeField]
        [Tooltip("Time in seconds between multi-cast messages")]
        [Range(1, 60)]
        protected float activeDiscoveryInterval = 3;

        private UdpClient serverUdpClient;
        private UdpClient clientUdpClient;

        #endregion

        #region Unity Callbacks

    #if UNITY_EDITOR
        private void Reset() { uniqueAppIdentifier = GetUniqueAppIdentifier(); }
    #endif

        // Made virtual so that inheriting classes' Start() can call base.Start() too
        protected virtual void Start()
        {
            // headless mode? then start advertising
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Logger.Log("Running on headless server. Starting advertising server to local network automatically.");
                StartAdvertisingServer();
            }
        }

        private void OnApplicationQuit()
        {
            // Ensure the ports are cleared no matter when Game/Unity UI exits
            Shutdown();
        }

        #endregion

        #region Common Methods

        /// <summary>
        /// Generates a unique game identifier.
        /// </summary>
        /// <remarks>
        /// This method can be overridden in child classes to implement custom identifier generation logic.
        /// When overriding, ensure that the value returned by the method is deterministic per your game project / version, and does not change between the calls to this method.
        /// For example, the default implementation gets this value from Hash128 of combined Application.version, Application.companyName and Application.productName values, which are consistent across a single Unity project.
        /// </remarks>
        /// <returns>Generated 64-bit integer ID.</returns>
        protected virtual long GetUniqueAppIdentifier()
        {
            var hash = new Hash128();
            hash.Append(Application.version);
            hash.Append(Application.companyName);
            hash.Append(Application.productName);
            byte[] hashBytes = Encoding.ASCII.GetBytes(hash.ToString());
            long id = BitConverter.ToInt64(hashBytes);

            return id;
        }

        /// <summary>
        /// Disposes UDP clients used for discovery communication and stops any running discovery routines.
        /// </summary>
        private void Shutdown()
        {
            CancelInvoke();

        #if UNITY_ANDROID
            // If we're on Android, then tell the Android OS that
            // we're done with multicasting and it may save battery again.
            EndMulticastLock();
        #endif

            // Helper function to shutdown a UDP client
            void ShutdownClient(ref UdpClient client)
            {
                if (client == null) return;

                try { client.Close(); }
                catch (SocketException)
                {
                    // If it's already closed, just swallow the error. There's no need to show it.
                }
            }

            // Shutdown all clients
            ShutdownClient(ref serverUdpClient);
            ShutdownClient(ref clientUdpClient);
        }

        #endregion

        #region Server Methods

        /// <summary>
        /// Starts advertising this server in the local network.
        /// </summary>
        public void StartAdvertisingServer()
        {
            if (!IsSupportedOnThisPlatform) throw new PlatformNotSupportedException("Network discovery not supported in this platform");

            StopDiscovery();

            // Setup port - may throw exception
            serverUdpClient = new UdpClient(serverBroadcastListenPort)
            {
                EnableBroadcast = true,
                MulticastLoopback = false
            };

            Logger.Log($"Started listening for server discovery requests on the local network (port {serverBroadcastListenPort}).\n" +
                       $"Unique app ID: {uniqueAppIdentifier}.");

            // listen for client pings
            ServerListenAsync().Forget();
        }

        /// <summary>
        /// Stops advertising this server in the local network.
        /// </summary>
        public void StopAdvertisingServer()
        {
            Shutdown();

            Logger.Log("Stopped listening for server discovery requests.");
        }

        /// <summary>
        /// Continuously listens to discovery requests in the local network.
        /// </summary>
        private async UniTask ServerListenAsync()
        {
        #if UNITY_ANDROID
            // Tell Android to allow us to use Multicasting.
            BeginMulticastLock();
        #endif

            while (true)
            {
                try { await ReceiveRequestAsync(serverUdpClient); }
                catch (ObjectDisposedException)
                {
                    // This socket's been disposed, that's okay, we'll handle it
                    break;
                }
                catch (Exception)
                {
                    // Invalid request or something else. Just ignore it.
                    // TODO: Maybe we should check for more explicit exceptions?
                }
            }
        }

        /// <summary>
        /// Receives a request from the client and processes it.
        /// </summary>
        /// <param name="udpClient"><see cref="UdpClient"/> to read the request with.</param>
        /// <exception cref="ProtocolViolationException">Thrown if the received request contained invalid handshake (i.e. different <see cref="uniqueAppIdentifier"/>).</exception>
        private async Task ReceiveRequestAsync(UdpClient udpClient)
        {
            // only proceed if there is available data in network buffer, or otherwise Receive() will block
            // average time for UdpClient.Available : 10 us

            UdpReceiveResult udpReceiveResult = await udpClient.ReceiveAsync();

            try
            {
                using (PooledNetworkReader networkReader = NetworkReaderPool.GetReader(udpReceiveResult.Buffer, null))
                {
                    long handshake = networkReader.ReadInt64();

                    bool isHandshakeValid = handshake == uniqueAppIdentifier;
                    Logger.Log($"Received discovery request, handshake is {handshake} ({(isHandshakeValid ? "VALID" : "INVALID")}).");

                    if (!isHandshakeValid)
                    {
                        // message is not for us
                        throw new ProtocolViolationException("Invalid handshake");
                    }

                    var request = networkReader.Read<TRequest>();
                    ProcessClientRequestWrapper(request, udpReceiveResult.RemoteEndPoint);
                }
            }
            catch (Exception ex) { Logger.LogException(ex); }
        }

        /// <summary>
        /// Reply to the client to inform it of this server.
        /// </summary>
        /// <param name="request">Request coming from a client.</param>
        /// <param name="endpoint">Address of the client that sent the request.</param>
        private void ProcessClientRequestWrapper(TRequest request, IPEndPoint endpoint)
        {
            var info = ProcessClientRequest(request, endpoint);

            using (PooledNetworkWriter writer = NetworkWriterPool.GetWriter())
            {
                try
                {
                    writer.WriteInt64(uniqueAppIdentifier);
                    writer.Write(info);

                    var data = writer.ToArraySegment();
                    // signature matches
                    // send response
                    serverUdpClient.Send(data.Array, data.Count, endpoint);
                }
                catch (Exception ex) { Logger.LogException(ex, this); }
            }
        }

        /// <summary>
        /// Process discovery request received from a client.
        /// </summary>
        /// <remarks>
        /// Use this method to craft server's response to the client.
        /// </remarks>
        /// <param name="request">Request coming from a client.</param>
        /// <param name="endpoint">Address of the client that sent the request.</param>
        /// <returns>The message to be sent back to the client.</returns>
        protected abstract TResponse ProcessClientRequest(TRequest request, IPEndPoint endpoint);

        #endregion

        #region Client Methods

        /// <summary>
        /// Makes this client start looking for servers on LAN.
        /// </summary>
        public void StartDiscovery()
        {
            if (!IsSupportedOnThisPlatform) throw new PlatformNotSupportedException("Network discovery not supported in this platform");

            Logger.Log("Initiating LAN server discovery...");

            StopDiscovery();

            try
            {
                // Setup port
                clientUdpClient = new UdpClient(0)
                {
                    EnableBroadcast = true,
                    MulticastLoopback = false
                };
            }
            catch (Exception)
            {
                // Free the port if we took it
                Shutdown();
                throw;
            }

            ClientListenAsync().Forget();

            InvokeRepeating(nameof(BroadcastDiscoveryRequest), 0, activeDiscoveryInterval);

            Logger.Log("Started LAN discovery.");
        }

        /// <summary>
        /// Makes this client stop looking for servers on LAN.
        /// </summary>
        public void StopDiscovery()
        {
            Shutdown();

            Logger.Log("Stopped LAN discovery.");
        }

        /// <summary>
        /// Broadcasts discovery request from this client to the machines on the local network.
        /// </summary>
        public void BroadcastDiscoveryRequest()
        {
            if (clientUdpClient == null) return;

            var endPoint = new IPEndPoint(IPAddress.Broadcast, serverBroadcastListenPort);

            using (PooledNetworkWriter writer = NetworkWriterPool.GetWriter())
            {
                writer.WriteInt64(uniqueAppIdentifier);

                try
                {
                    TRequest request = CraftDiscoveryRequest();
                    writer.Write(request);

                    var data = writer.ToArraySegment();

                    clientUdpClient.SendAsync(data.Array, data.Count, endPoint);
                }
                catch (Exception ex) { Logger.LogException(ex); }
            }
        }

        /// <summary>
        /// Awaits for server response.
        /// </summary>
        /// <returns>ClientListenAsync Task.</returns>
        private async UniTask ClientListenAsync()
        {
            while (true)
            {
                try { await ReceiveGameBroadcastAsync(clientUdpClient); }
                catch (ObjectDisposedException)
                {
                    // socket was closed, no problem
                    return;
                }
                catch (Exception ex) { Logger.LogException(ex); }
            }
        }

        /// <summary>
        /// Receives a response from the server and processes it.
        /// </summary>
        /// <param name="udpClient"><see cref="UdpClient"/> to read the response with.</param>
        private async Task ReceiveGameBroadcastAsync(UdpClient udpClient)
        {
            // only proceed if there is available data in network buffer, or otherwise Receive() will block
            // average time for UdpClient.Available : 10 us
            UdpReceiveResult udpReceiveResult = await udpClient.ReceiveAsync();

            try
            {
                using (PooledNetworkReader networkReader = NetworkReaderPool.GetReader(udpReceiveResult.Buffer, null))
                {
                    if (networkReader.ReadInt64() != uniqueAppIdentifier) return;

                    var response = networkReader.Read<TResponse>();

                    ProcessServerResponse(response, udpReceiveResult.RemoteEndPoint);
                }
            }
            catch (Exception ex) { Logger.LogException(ex); }
        }

        /// <summary>
        /// Create a message that will be broadcast on the network to discover servers.
        /// </summary>
        /// <remarks>
        /// Use this method to craft your discovery request message, filling it with required data (e.g. desired game mode, language, difficulty, etc.).
        /// </remarks>
        /// <returns>An instance of <see cref="ServerRequest"/> with data to be broadcast.</returns>
        protected abstract TRequest CraftDiscoveryRequest();

        /// <summary>
        /// Process the answer from a server.
        /// </summary>
        /// <remarks>
        /// Implementations can execute arbitrary logic here to react to each new server discovery.
        /// For example, an event can be raised to notify that a server was found.
        /// </remarks>
        /// <param name="response">Response that came from the server.</param>
        /// <param name="endpoint">Address of the server that replied.</param>
        protected abstract void ProcessServerResponse(TResponse response, IPEndPoint endpoint);

        #endregion

        #region Android-specific functions for Multicasting support

    #if UNITY_ANDROID
        AndroidJavaObject multicastLock;
        private bool hasMulticastLock;

        private void BeginMulticastLock() {
            if (hasMulticastLock)
                return;

            if (Application.platform == RuntimePlatform.Android)
            {
                using (AndroidJavaObject activity = new AndroidJavaClass("com.unity3d.player.UnityPlayer").GetStatic<AndroidJavaObject>("currentActivity"))
                {
                    using (AndroidJavaObject wifiManager = activity.Call<AndroidJavaObject>("getSystemService", "wifi"))
                    {
                        multicastLock = wifiManager.Call<AndroidJavaObject>("createMulticastLock", "lock");
                        multicastLock.Call("acquire");
                        hasMulticastLock = true;
                    }
                }
			}
        }

        private void EndMulticastLock()
        {
            // Don't have a multicast lock? Short-circuit.
            if (!hasMulticastLock)
                return;

            // Release the lock.
            multicastLock?.Call("release");
            hasMulticastLock = false;
        }
    #endif

        #endregion
    }
}
