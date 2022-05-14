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
    using System.Threading.Tasks;
    using Cysharp.Threading.Tasks;
    using Logging;
    using Serialization;
    using UnityEditor;
    using UnityEngine;
    using UnityEngine.Rendering;
    using Random = UnityEngine.Random;

    /// <summary>
    /// Base implementation for Network Discovery. Extend this component to provide custom discovery with game specific data.
    /// </summary>
    /// <remarks>
    /// See <see cref="NetworkDiscovery">NetworkDiscovery</see> for a sample implementation.
    /// </remarks>
    [DisallowMultipleComponent]
    [HelpURL("https://miragenet.github.io/Mirage/Articles/Components/NetworkDiscovery.html")]
    public abstract class NetworkDiscoveryBase<TRequest, TResponse> : MonoBehaviour
    {
        #region Variables / Properties

        private static readonly ILogger Logger = LogFactory.GetLogger(typeof(NetworkDiscoveryBase<TRequest, TResponse>));

        public static bool IsSupportedOnThisPlatform => Application.platform != RuntimePlatform.WebGLPlayer;

        // each game should have a random unique handshake, this way you can tell if this is the same game or not
        // [HideInInspector]
        [ReadOnlyInspector]
        public long secretHandshake;

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

        #region Static Methods

        public static long RandomLong()
        {
            int value1 = Random.Range(int.MinValue, int.MaxValue);
            int value2 = Random.Range(int.MinValue, int.MaxValue);
            return value1 + ((long)value2 << 32);
        }

        #endregion

        #region Unity Callbacks

    #if UNITY_EDITOR
        private void OnValidate()
        {
            if (secretHandshake == 0) secretHandshake = RandomLong();
        }
    #endif

        // Made virtual so that inheriting classes' Start() can call base.Start() too
        public virtual void Start()
        {
            // headless mode? then start advertising
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) { AdvertiseServer(); }
        }

        private void OnApplicationQuit()
        {
            // Ensure the ports are cleared no matter when Game/Unity UI exits
            Shutdown();
        }

        #endregion

        private void Shutdown()
        {
        #if UNITY_ANDROID
            // If we're on Android, then tell the Android OS that
            // we're done with multicasting and it may save battery again.
            EndMulticastLock();
        #endif

            if (serverUdpClient != null)
            {
                try { serverUdpClient.Close(); }
                catch (Exception)
                {
                    // If it's already closed, then just swallow the error. There's no need to show it.
                }

                serverUdpClient = null;
            }

            if (clientUdpClient != null)
            {
                try { clientUdpClient.Close(); }
                catch (Exception)
                {
                    // Again, if it's already closed, then just swallow the error.
                }

                clientUdpClient = null;
            }

            CancelInvoke();
        }

        #region Server Methods

        /// <summary>
        /// Starts advertising this server in the local network.
        /// </summary>
        public void AdvertiseServer()
        {
            if (!IsSupportedOnThisPlatform) throw new PlatformNotSupportedException("Network discovery not supported in this platform");

            StopDiscovery();

            // Setup port -- may throw exception
            serverUdpClient = new UdpClient(serverBroadcastListenPort)
            {
                EnableBroadcast = true,
                MulticastLoopback = false
            };

            // listen for client pings
            ServerListenAsync().Forget();
        }

        public async UniTask ServerListenAsync()
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

        private async Task ReceiveRequestAsync(UdpClient udpClient)
        {
            // only proceed if there is available data in network buffer, or otherwise Receive() will block
            // average time for UdpClient.Available : 10 us

            UdpReceiveResult udpReceiveResult = await udpClient.ReceiveAsync();

            using (PooledNetworkReader networkReader = NetworkReaderPool.GetReader(udpReceiveResult.Buffer, null))
            {
                long handshake = networkReader.ReadInt64();
                if (handshake != secretHandshake)
                {
                    // message is not for us
                    throw new ProtocolViolationException("Invalid handshake");
                }

                var request = networkReader.Read<TRequest>();

                ProcessClientRequest(request, udpReceiveResult.RemoteEndPoint);
            }
        }

        /// <summary>
        /// Reply to the client to inform it of this server.
        /// </summary>
        /// <remarks>
        /// Override this method if you wish to ignore server requests based on custom criteria such as language, server game mode or difficulty.
        /// </remarks>
        /// <param name="request">Request coming from a client.</param>
        /// <param name="endpoint">Address of the client that sent the request.</param>
        protected virtual void ProcessClientRequest(TRequest request, IPEndPoint endpoint)
        {
            TResponse info = ProcessRequest(request, endpoint);

            if (info == null) return;

            using (PooledNetworkWriter writer = NetworkWriterPool.GetWriter())
            {
                try
                {
                    writer.WriteInt64(secretHandshake);
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
        /// Process the request from a client.
        /// </summary>
        /// <remarks>
        /// Override this method if you wish to provide more information to the clients such as the name of the host player.
        /// </remarks>
        /// <param name="request">Request coming a from client.</param>
        /// <param name="endpoint">Address of the client that sent the request.</param>
        /// <returns>The message to be sent back to the client or null.</returns>
        protected abstract TResponse ProcessRequest(TRequest request, IPEndPoint endpoint);

        #endregion

        #region Client Methods

        /// <summary>
        /// Start active discovery.
        /// </summary>
        public void StartDiscovery()
        {
            if (!IsSupportedOnThisPlatform) throw new PlatformNotSupportedException("Network discovery not supported in this platform");

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
        }

        /// <summary>
        /// Stop active discovery.
        /// </summary>
        public void StopDiscovery() { Shutdown(); }

        /// <summary>
        /// Awaits for server response.
        /// </summary>
        /// <returns>ClientListenAsync Task.</returns>
        public async UniTask ClientListenAsync()
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
        /// Sends discovery request from client.
        /// </summary>
        public void BroadcastDiscoveryRequest()
        {
            if (clientUdpClient == null) return;

            var endPoint = new IPEndPoint(IPAddress.Broadcast, serverBroadcastListenPort);

            using (PooledNetworkWriter writer = NetworkWriterPool.GetWriter())
            {
                writer.WriteInt64(secretHandshake);

                try
                {
                    TRequest request = GetRequest();
                    writer.Write(request);

                    var data = writer.ToArraySegment();

                    clientUdpClient.SendAsync(data.Array, data.Count, endPoint);
                }
                catch (Exception)
                {
                    // It is ok if we can't broadcast to one of the addresses.
                    // TODO: Maybe we should check for more explicit exceptions?
                }
            }
        }

        /// <summary>
        /// Create a message that will be broadcast on the network to discover servers.
        /// </summary>
        /// <remarks>
        /// Override this method if you wish to include additional data in the discovery message such as desired game mode, language, difficulty, etc.
        /// </remarks>
        /// <returns>An instance of <see cref="ServerRequest"/> with data to be broadcast.</returns>
        protected abstract TRequest GetRequest();

        private async Task ReceiveGameBroadcastAsync(UdpClient udpClient)
        {
            // only proceed if there is available data in network buffer, or otherwise Receive() will block
            // average time for UdpClient.Available : 10 us

            UdpReceiveResult udpReceiveResult = await udpClient.ReceiveAsync();

            using (PooledNetworkReader networkReader = NetworkReaderPool.GetReader(udpReceiveResult.Buffer, null))
            {
                if (networkReader.ReadInt64() != secretHandshake) return;

                var response = networkReader.Read<TResponse>();

                ProcessResponse(response, udpReceiveResult.RemoteEndPoint);
            }
        }

        /// <summary>
        /// Process the answer from a server.
        /// </summary>
        /// <remarks>
        /// A client receives a reply from a server, this method processes the reply and raises an event.
        /// </remarks>
        /// <param name="response">Response that came from the server</param>
        /// <param name="endpoint">Address of the server that replied</param>
        protected abstract void ProcessResponse(TResponse response, IPEndPoint endpoint);

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
