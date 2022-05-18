namespace Mirage.Discovery
{
    using System.Collections.Generic;
    using System.Linq;
    using UnityEngine;
#if UNITY_EDITOR
    using UnityEditor.Events;
#endif

    [DisallowMultipleComponent]
    [AddComponentMenu("Network/Lan Discovery Hud")]
    [HelpURL("https://miragenet.github.io/Mirage/Articles/Components/LanDiscovery.html")]
    public class LanDiscoveryHud : MonoBehaviour
    {
        readonly Dictionary<long, ServerResponse> discoveredServers = new();
        Vector2 scrollViewPos = Vector2.zero;

        public LanDiscovery lanDiscovery;

        public NetworkManager networkManager;

#if UNITY_EDITOR
        void OnValidate()
        {
            if (lanDiscovery == null)
            {
                lanDiscovery = FindObjectOfType<LanDiscovery>();
                UnityEventTools.AddPersistentListener(lanDiscovery.OnServerFound, OnDiscoveredServer);
            }

            if (networkManager == null)
            {
                networkManager = GetComponent<NetworkManager>();
            }
        }
#endif

        void OnGUI()
        {
            if (networkManager.Server.Active || networkManager.Client.Active)
                return;

            if (!networkManager.Client.IsConnected && !networkManager.Server.Active && !networkManager.Client.Active)
                DrawGUI();
        }

        void DrawGUI()
        {
            GUILayout.BeginHorizontal();

            if (GUILayout.Button("Find Servers"))
            {
                discoveredServers.Clear();
                lanDiscovery.StartDiscovery();
            }

            // LAN Host
            if (GUILayout.Button("Start Host"))
            {
                discoveredServers.Clear();
                networkManager.Server.StartServer(networkManager.Client);
                lanDiscovery.StartAdvertisingServer();
            }

            // Dedicated server
            if (GUILayout.Button("Start Server"))
            {
                discoveredServers.Clear();
                networkManager.Server.StartServer();

                lanDiscovery.StartAdvertisingServer();
            }

            GUILayout.EndHorizontal();

            // show list of found server

            GUILayout.Label($"Discovered Servers [{discoveredServers.Count}]:");

            // servers
            scrollViewPos = GUILayout.BeginScrollView(scrollViewPos);

            foreach (ServerResponse info in discoveredServers.Values)
                if (GUILayout.Button(info.EndPoint.Address.ToString()))
                    Connect(info);

            GUILayout.EndScrollView();
        }

        void Connect(ServerResponse info)
        {
            networkManager.Client.Connect(info.uri.First());
        }

        public void OnDiscoveredServer(ServerResponse info)
        {
            // Note that you can check the versioning to decide if you can connect to the server or not using this method
            discoveredServers[info.serverId] = info;
        }
    }
}
