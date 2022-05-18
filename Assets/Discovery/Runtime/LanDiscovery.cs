namespace Mirage.Discovery
{
    using System;
    using System.Net;
    using Logging;
    using SocketLayer;
    using UnityEngine;
    using UnityEngine.Events;
    using Random = UnityEngine.Random;

    [Serializable]
    public class ServerFoundUnityEvent : UnityEvent<ServerResponse> { };

    [DisallowMultipleComponent]
    [AddComponentMenu("Network/LanDiscoveryLan Discovery")]
    public class LanDiscovery : LanDiscoveryBase<ServerRequest, ServerResponse>
    {
        private static readonly ILogger Logger = LogFactory.GetLogger(typeof(LanDiscovery));

        #region Server Methods

        public long ServerId { get; private set; }

        [Tooltip("Transport to be advertised during discovery")]
        public SocketFactory transport;

        [Tooltip("Invoked when a server is found")]
        public ServerFoundUnityEvent OnServerFound = new();

        public static long RandomLong()
        {
            int value1 = Random.Range(int.MinValue, int.MaxValue);
            int value2 = Random.Range(int.MinValue, int.MaxValue);
            return value1 + ((long)value2 << 32);
        }

        protected override void Start()
        {
            ServerId = RandomLong();

            base.Start();
        }

        /// <summary>
        /// Process the request from a client.
        /// </summary>
        /// <remarks>
        /// Override if you wish to provide more information to the clients
        /// such as the name of the host player
        /// </remarks>
        /// <param name="request">Request coming from a client.</param>
        /// <param name="endpoint">Address of the client that sent the request.</param>
        /// <returns>The message to be sent back to the client or null.</returns>
        protected override ServerResponse ProcessClientRequest(ServerRequest request, IPEndPoint endpoint)
        {
            // In this case we don't do anything with the request
            // but other discovery implementations might want to use the data
            // in there,  This way the client can ask for
            // specific game mode or something

            try
            {
                // this is an example reply message,  return your own
                // to include whatever is relevant for your game
                return new ServerResponse
                {
                    serverId = ServerId,
                    uri = new[] { transport.GetBindEndPoint().ToString() }
                };
            }
            catch (NotImplementedException)
            {
                Logger.LogError($"Transport {transport} does not support network discovery");
                throw;
            }
        }

        #endregion

        #region Client Methods

        /// <summary>
        /// Create a message that will be broadcast on the network to discover servers
        /// </summary>
        /// <remarks>
        /// Override if you wish to include additional data in the discovery message
        /// such as desired game mode, language, difficulty, etc... </remarks>
        /// <returns>An instance of ServerRequest with data to be broadcast</returns>
        protected override ServerRequest CraftDiscoveryRequest() => new ServerRequest();

        /// <summary>
        /// Process the answer from a server.
        /// </summary>
        /// <remarks>
        /// A client receives a reply from a server, this method processes the reply and raises an event.
        /// </remarks>
        /// <param name="response">Response that came from the server</param>
        /// <param name="endpoint">Address of the server that replied</param>
        protected override void ProcessServerResponse(ServerResponse response, IPEndPoint endpoint)
        {
            // we received a message from the remote endpoint
            response.EndPoint = endpoint;

            // although we got a supposedly valid url, we may not be able to resolve
            // the provided host
            // However we know the real ip address of the server because we just
            // received a packet from it, so use that as host.

            response.uri = new[]
            {
                response.EndPoint.Address.ToString()
            };
            Logger.Log($"Received response from address {response.uri[0]}");

            OnServerFound.Invoke(response);
        }

        #endregion
    }
}
